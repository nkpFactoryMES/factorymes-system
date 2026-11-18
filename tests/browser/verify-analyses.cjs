// 설비 분석(Mock AI) 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-통계-AI-재처리-동시구현分担-v1.md "2단계 확정 규격".
//   작업자 메뉴·화면 없음, API 403 → 관리자 Mock 대상 등록(화면)
//   → 고유 설비 + 1초 수집(실제 시뮬레이터) 1분 이상 → 화면에서 최근 2분 TREND_SUMMARY 요청
//   → 첫 전송은 브라우저에서 끊어(통신 오류) 같은 요청 ID로만 재전송되는지 확인
//   → Jobs·Gateway·AI Connector 동기 Mock 경로로 결과: MOCK, MockFlag, UNKNOWN, Accepted false,
//     근거 해시가 입력 고정본과 일치 → 상세 팝업에 Mock·판정 안 함·참고용 표시
//   → 같은 요청 재전송은 같은 작업, 내용이 다르면 409 → 데이터 없는 설비는 INSUFFICIENT_DATA·결과 0
//   → 해당 설비에 경보 0건, 390px 넘침 없음, 캡처.
// 정리: 수집 연결만 중지한다(이력·Mock 대상은 남긴다). 실제 AI 호출은 없다.
// 실행: GPT 배포 뒤 `node verify-analyses.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'AIV-' + crypto.randomUUID().slice(0, 8).toUpperCase();

// 설비 수집 연결과 AI 대상은 같은 INTEGRATION_TARGET 코드 공간(UQ_TGT_CODE)을 쓰므로 ID를 나눈다.
const aiKey = key + 'AI';

const checks = [];
const errors = [];
const contexts = [];

function check(name) {
  checks.push(name);
  console.log('PASS ' + name);
}

function button(scope, name) {
  return scope.getByRole('button', {
    name,
    exact: true
  });
}

// 화면 상단 상태 문구가 기대값을 포함하고 처리 중 표시가 풀릴 때까지 기다린다.
async function status(page, text) {
  await page.waitForFunction(
    expected => document.querySelector('main > p[role="status"]')?.textContent.includes(expected)
      && !document.querySelector('main > fieldset')?.disabled,
    text
  );
}

async function login(browser, user, secret, viewport) {
  const context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport
  });
  contexts.push(context);

  // 테스트 브라우저는 localhost 밖으로 요청하지 않는다.
  await context.route('**/*', route => {
    const hostname = new URL(route.request().url()).hostname;
    return hostname === 'localhost' ? route.continue() : route.abort();
  });

  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  await page.goto(base);
  await page.locator('#login-form').waitFor({ state: 'visible' });
  await page.locator('#login').fill(user);
  await page.locator('#password').fill(
    fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets'), secret), 'utf8').trim()
  );
  await button(page, '로그인').click();
  await page.locator('#workspace').waitFor({ state: 'visible' });
  return page;
}

// 로그인한 브라우저의 쿠키와 CSRF로 API를 호출한다(fixture 준비·확인용).
async function api(page, method, url, body) {
  return page.evaluate(
    async ({ method, url, body }) => {
      const csrf = await (await fetch('/api/v1/auth/csrf', { cache: 'no-store' })).json();
      const response = await fetch(url, {
        method,
        cache: 'no-store',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': csrf.requestToken
        },
        body: body === undefined ? undefined : JSON.stringify(body)
      });
      const text = await response.text();
      return {
        status: response.status,
        json: text ? JSON.parse(text) : null
      };
    },
    {
      method,
      url,
      body
    }
  );
}

// expected는 숫자 하나 또는 허용 목록이다.
async function expectStatus(page, method, url, body, expected, label) {
  const result = await api(page, method, url, body);
  const allowed = Array.isArray(expected) ? expected : [expected];
  if (!allowed.includes(result.status)) {
    throw new Error(`${label}: HTTP ${result.status} ${JSON.stringify(result.json)}`);
  }
  return result.json;
}

async function waitUntil(action, condition, label, timeoutMs = 120000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await action();
    if (condition(result)) {
      return result;
    }
    await new Promise(resolve => setTimeout(resolve, 2000));
  }
  throw new Error(label);
}

async function noPageOverflow(page, label) {
  const extra = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  if (extra > 1) {
    throw new Error(`${label}: page is ${extra}px wider than viewport`);
  }
}

// 서버 UTC 시각(X-Mes-Time). 호스트와 컨테이너 시계가 다를 수 있어 서버 값을 쓴다.
async function serverNow(page) {
  const header = await page.evaluate(async () => {
    const response = await fetch('/api/v1/auth/csrf', { cache: 'no-store' });
    return response.headers.get('X-Mes-Time');
  });
  if (!header) {
    throw new Error('Server time header X-Mes-Time is missing');
  }
  return new Date(Date.parse(header));
}

// 화면 입력(datetime-local)은 한국 시간 "YYYY-MM-DDTHH:mm"이다.
function kstLocal(date) {
  return new Date(date.getTime() + 9 * 3600 * 1000).toISOString().slice(0, 16);
}

function floorMinute(date) {
  return new Date(Math.floor(date.getTime() / 60000) * 60000);
}

// 작업 ID는 GUID 36자다. 화면 글자에서 뽑지 않고 API 응답 값을 그대로 쓴다.
const jobIdPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

// 작업 조회: 200이 아니면(Problem 응답 등) null을 돌려 "완료"로 오인하지 않게 한다.
async function getJob(page, jobId) {
  const result = await api(page, 'GET', '/api/v1/analyses/' + encodeURIComponent(jobId));
  if (result.status !== 200 || !result.json || !Array.isArray(result.json.results)) {
    console.log(`JOB ${jobId} HTTP ${result.status}`);
    return null;
  }
  return result.json;
}

const finished = job => job !== null && typeof job.state === 'string'
  && (job.results.length > 0 || ['FAILED', 'TIMED_OUT', 'INSUFFICIENT_DATA'].includes(job.state));

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });
  const desktop = {
    width: 1360,
    height: 1000
  };

  let admin = null;
  let targetCreated = false;

  // 실패 원인을 남기기 위한 화면 요청 기록(POST 본문).
  const sent = [];

  try {
    admin = await login(browser, 'admin', 'admin_password', desktop);
    const worker = await login(browser, 'portal', 'portal_password', desktop);

    // 0. 권한: 작업자는 메뉴·화면·API 모두 없음
    await worker.goto(base + '/analyses');
    await worker.getByText('설비 분석은 관리자만 사용할 수 있습니다.').waitFor();
    // 업무 메뉴(nav.shell-menu)가 그려진 뒤에 관리자 메뉴가 없는지 센다(빈 메뉴로 통과하지 않게).
    await worker.locator('nav.shell-menu a').first().waitFor({ state: 'attached' });
    if (await worker.locator('nav.shell-menu a').filter({ hasText: '설비 분석' }).count()) {
      throw new Error('Worker must not see analyses menu');
    }
    const denied = await api(worker, 'GET', '/api/v1/analyses');
    if (denied.status !== 403) {
      throw new Error('Worker analyses API returned ' + denied.status);
    }
    check('Worker has no analyses menu, page, or API (403)');

    // 1. fixture: 고유 설비 + 1초 수집(실제 시뮬레이터), 데이터 없는 설비 하나
    const lines = await expectStatus(
      admin,
      'GET',
      '/api/v1/lines?active=true&limit=100',
      undefined,
      200,
      'Lines'
    );
    const lineId = lines.items.find(x => x.code === 'DEMO-LINE').lineId;
    const equipment = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: key,
        name: '분석 검증 설비 ' + key,
        lineId,
        active: true
      },
      201,
      'Create equipment'
    );
    const quiet = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: key + 'Q',
        name: '분석 검증 무수집 설비 ' + key,
        lineId,
        active: true
      },
      201,
      'Create quiet equipment'
    );
    await expectStatus(
      admin,
      'POST',
      '/api/v1/integration-targets',
      {
        targetId: key,
        displayName: '분석 검증 연결 ' + key,
        connectorType: 'equipment-http',
        enabled: true,
        kind: 'EQUIPMENT',
        equipmentId: equipment.equipmentId,
        settings: {
          protocol: 'HTTP',
          approvedEndpoint: 'http://equipment-simulator:8080/equipment/' + key,
          pollIntervalMs: 1000,
          timeoutMs: 1000,
          staleSec: 20,
          credentialRef: null,
          mappings: [
            {
              mappingId: null,
              metricCode: 'TEMPERATURE',
              sourcePath: 'temperature',
              dataType: 'DECIMAL',
              valueKind: 'NUMBER',
              unit: 'C',
              scale: '1',
              offset: '0',
              enabled: true
            }
          ]
        }
      },
      201,
      'Register 1s collection'
    );
    targetCreated = true;
    const collectionStart = await serverNow(admin);
    check('Fixture: equipment with real 1s collection and a quiet equipment');

    // 2. 관리자 화면: Mock 안내, Mock 대상 등록
    await admin.goto(base + '/analyses');
    await admin.getByRole('heading', {
      name: '설비 분석(Mock·로컬 AI)',
      exact: true
    }).waitFor();
    await status(admin, '완료했습니다.');
    await admin.locator('.mock-notice').filter({ hasText: '실제 AI 예측이 아니고' }).waitFor();

    // 대상 등록은 상단 "대상 등록" 팝업, 대상 표는 "분석 대상" 탭이다.
    await button(admin.locator('main .toolbar'), '대상 등록').click();
    const targetForm = admin.locator('form.target-create');
    await targetForm.waitFor();
    await targetForm.getByLabel('대상 ID').fill(aiKey);
    await targetForm.getByLabel('대상 이름').fill('분석 검증 Mock ' + aiKey);
    await button(targetForm, 'Mock 대상 등록').click();
    await status(admin, 'Mock 분석 대상을 등록했습니다.');
    await targetForm.waitFor({ state: 'detached' });
    await admin.getByRole('tab', {
      name: '분석 대상',
      exact: true
    }).click();
    const targets = admin.locator('section.ai-targets');
    await targets.locator('tbody tr')
      .filter({ hasText: aiKey })
      .filter({ hasText: 'MOCK · mock-trend-v1' })
      .waitFor();
    check('Admin sees Mock-only notice, registers a Mock AI target');

    // 3. 수집이 분 경계를 넘어 1분 이상 쌓일 때까지 기다린다(관측 비율 확보).
    let periodEnd = null;
    await waitUntil(
      async () => {
        periodEnd = floorMinute(await serverNow(admin));
        return periodEnd.getTime() - collectionStart.getTime();
      },
      elapsed => elapsed >= 60000,
      'Collection did not span a full minute',
      180000
    );
    const periodStart = new Date(periodEnd.getTime() - 2 * 60000);

    // 4. 화면에서 요청. 첫 전송은 브라우저에서 끊어 통신 오류를 만든다.
    const request = admin.locator('section.analysis-request');
    await admin.reload();
    await status(admin, '완료했습니다.');
    // 분석 요청은 상단 "새 분석 요청" 팝업이다.
    await button(admin.locator('main .toolbar'), '새 분석 요청').click();
    await request.waitFor();
    // 입력은 원시 행 자르기가 아니라 Oracle 1분 사전 집계라는 안내(24시간·50종류 제한은 유지).
    // 이 안내는 "새 분석 요청" 팝업 안에 있다.
    await request.locator('.aggregation-note')
      .filter({ hasText: '1분 단위로 미리 집계한 값' })
      .filter({ hasText: '지표 50종류' })
      .waitFor();
    check('Request popup shows the pre-aggregation input notice');
    await request.getByLabel('분석할 설비').selectOption(equipment.equipmentId);
    await request.getByLabel('분석 대상').selectOption(aiKey);
    const startInput = request.getByLabel('시작(한국 시간)');
    await startInput.fill(kstLocal(periodStart));
    await startInput.dispatchEvent('change');
    const endInput = request.getByLabel('끝(한국 시간)');
    await endInput.fill(kstLocal(periodEnd));
    await endInput.dispatchEvent('change');
    await request.getByLabel('분석 종류').selectOption('TREND_SUMMARY');

    // 브라우저가 실제로 가진 입력값을 확인하고 남긴다(형식 문제를 바로 보기 위해).
    const startValue = await startInput.inputValue();
    const endValue = await endInput.inputValue();
    console.log(`INPUT start=${startValue} end=${endValue}`);
    if (startValue !== kstLocal(periodStart) || endValue !== kstLocal(periodEnd)) {
      throw new Error(`Period inputs differ: start=${startValue} end=${endValue}`);
    }

    let aborted = 0;
    const isAnalyses = url => url.pathname === '/api/v1/analyses';
    const capture = route => {
      if (route.request().method() !== 'POST') {
        return route.fallback();
      }
      sent.push(JSON.parse(route.request().postData()));
      if (aborted === 0) {
        aborted += 1;
        return route.abort('failed');
      }
      return route.fallback();
    };
    await admin.route(isAnalyses, capture);
    await button(request, '분석 요청').click();
    await status(admin, '네트워크 오류');
    await button(request, '같은 요청 다시 보내기').waitFor();
    if (await button(request, '분석 요청').count()) {
      throw new Error('New request must be blocked until the same request is resent');
    }
    // 재전송 응답(200/202)의 jobId를 그대로 잡는다.
    const accepted = admin.waitForResponse(response =>
      new URL(response.url()).pathname === '/api/v1/analyses'
        && response.request().method() === 'POST'
        && [200, 202].includes(response.status())
    );
    await button(request, '같은 요청 다시 보내기').click();
    const acceptedJob = await (await accepted).json();
    await status(admin, '분석을 요청했습니다.');
    await admin.unroute(isAnalyses, capture);
    if (sent.length !== 2 || JSON.stringify(sent[0]) !== JSON.stringify(sent[1])) {
      throw new Error('Resend must reuse the same request body: ' + JSON.stringify(sent));
    }
    const body = sent[1];
    check('Network failure blocks new requests; resend reuses the same request id and body');

    // 5. 결과: Jobs → Gateway → AI Connector 동기 Mock 경로
    const detail = admin.locator('section.analysis-detail');
    await detail.waitFor();
    const jobId = acceptedJob.jobId;
    if (!jobIdPattern.test(jobId ?? '')) {
      throw new Error('POST response has no GUID jobId: ' + JSON.stringify(acceptedJob));
    }
    await detail.locator('h3').filter({ hasText: '#' + jobId }).waitFor();
    const job = await waitUntil(
      () => getJob(admin, jobId),
      finished,
      'Analysis job did not finish'
    );
    if (job.results.length !== 1) {
      throw new Error('Expected one Mock result: ' + JSON.stringify(job));
    }
    const result = job.results[0];
    const evidence = result.evidence;
    if (result.provider !== 'MOCK' || result.model !== 'mock-trend-v1' || !result.mockFlag
      || result.risk !== 'UNKNOWN' || result.accepted !== false
      || evidence.inputHash !== job.snapshot.inputHash
      || !evidence.summary.includes('실제 예측이 아니며')
      || evidence.observations.length < 1 || evidence.observations.length > 20) {
      throw new Error('Mock result unexpected: ' + JSON.stringify(job));
    }
    check('Mock result: MOCK, mock-trend-v1, MockFlag, UNKNOWN, not accepted, hash bound');

    await button(detail, '상세 새로고침').click();
    await status(admin, '완료했습니다.');
    const article = detail.locator('article.result').first();
    await article.getByText('MOCK', { exact: true }).waitFor();
    await article.getByText('위험도 UNKNOWN · 판정 안 함').waitFor();
    await article.getByText('참고용 · 채택 안 함').waitFor();
    await article.locator('.summary').filter({ hasText: '모의(Mock) 결과입니다' }).waitFor();
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-analyses-screen.png'),
      fullPage: true
    });
    await button(detail, '닫기').click();
    await detail.waitFor({ state: 'detached' });
    check('Detail popup shows Mock, no risk verdict, reference-only result; capture saved');

    // 6. 멱등: 같은 요청은 같은 작업, 같은 ID에 다른 내용은 409
    const replay = await expectStatus(
      admin,
      'POST',
      '/api/v1/analyses',
      body,
      [200, 202],
      'Replay same request'
    );
    if (replay.jobId !== jobId) {
      throw new Error('Replay created another job: ' + replay.jobId);
    }
    const changed = await api(admin, 'POST', '/api/v1/analyses', {
      ...body,
      analysisKind: 'MAINTENANCE_RISK'
    });
    if (changed.status !== 409) {
      throw new Error('Changed request with same id returned ' + changed.status);
    }
    check('Same request id returns the same job; changed content is refused (409)');

    // 7. 데이터 없는 설비: INSUFFICIENT_DATA, 결과 0
    const empty = await expectStatus(
      admin,
      'POST',
      '/api/v1/analyses',
      {
        requestId: crypto.randomUUID(),
        equipmentId: quiet.equipmentId,
        targetId: aiKey,
        periodStart: periodStart.toISOString(),
        periodEnd: periodEnd.toISOString(),
        analysisKind: 'TREND_SUMMARY'
      },
      [200, 202],
      'Request for quiet equipment'
    );
    const emptyJob = await waitUntil(
      () => getJob(admin, empty.jobId),
      finished,
      'Quiet equipment job did not finish'
    );
    if (emptyJob.state !== 'INSUFFICIENT_DATA' || emptyJob.results.length !== 0) {
      throw new Error('Quiet equipment must be INSUFFICIENT_DATA: ' + JSON.stringify(emptyJob));
    }
    check('Equipment without data ends INSUFFICIENT_DATA with no result');

    // 8. Mock 결과로 경보가 생기지 않는다
    const alarms = await expectStatus(
      admin,
      'GET',
      `/api/v1/alarms?equipmentId=${equipment.equipmentId}`,
      undefined,
      200,
      'Alarms for analysed equipment'
    );
    if (alarms.items.length !== 0) {
      throw new Error('Mock analysis must not create alarms: ' + JSON.stringify(alarms.items));
    }
    check('Mock analysis creates no maintenance alarm');

    // 9. 390px: 본문 넘침 없음
    const phone = await login(browser, 'admin', 'admin_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/analyses');
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone analyses');
    check('Phone width 390: no page overflow');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      testKey: key,
      aiTargetKey: aiKey,
      jobId,
      insufficientJobId: empty.jobId,
      cloudApiCalls: 0,
      localModelCalls: 0,
      maintenanceAlarms: 0,
      note: 'Mock AI only; collection disabled after test; history and Mock target retained. '
        + 'Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-analyses-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser analyses verified');
  } catch (error) {
    console.error(error.message);
    if (admin) {
      const request = admin.locator('section.analysis-request');
      const start = await request.getByLabel('시작(한국 시간)')
        .inputValue({ timeout: 1000 })
        .catch(() => 'none');
      const end = await request.getByLabel('끝(한국 시간)')
        .inputValue({ timeout: 1000 })
        .catch(() => 'none');
      console.error(`INPUT start=${start} end=${end} POST sent=${sent.length}`);
    }
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const text = await page.locator('main > p[role="status"]')
          .textContent({ timeout: 1000 })
          .catch(() => 'none');
        console.error('PAGE STATUS: ' + text);
      }
    }
    process.exitCode = 1;
  } finally {
    // 정리: 수집 연결만 중지한다(이력은 남긴다).
    if (admin && targetCreated) {
      try {
        const current = await api(admin, 'GET', '/api/v1/integration-targets/' + key);
        if (current.status === 200 && current.json.enabled) {
          await api(admin, 'PUT', `/api/v1/integration-targets/${key}/state`, {
            expectedVersion: current.json.version,
            expectedConfigVersion: current.json.configVersion,
            enabled: false,
            reason: 'Browser analyses verification complete'
          });
        }
      } catch (cleanupError) {
        console.error('CLEANUP: ' + cleanupError.message);
        process.exitCode = 1;
      }
    }
    await browser.close();
  }
})();

