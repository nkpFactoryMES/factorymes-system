// 로컬 AI(LOCAL, GPU qwen2.5:1.5b) 설비 분석 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-로컬AI-분담규격-v1.md, 23:47·23:48·23:53·23:57 보완.
// 새 GPU 추론을 만들지 않는다. GPT 통합 시험(LocalAiVerification)이 남긴
// verification-results/local-ai-verification.json의 실제 결과를 화면에서 조회한다.
//   jobId(실제 GPU, 규칙 위험도 report.risk), unknownJobId(표본 부족 → UNKNOWN),
//   noDataJobId(INSUFFICIENT_DATA, 결과 0)
//   → 화면: 제목·안내, 대상 등록 팝업에서 공급자 LOCAL 선택(주소·키·모델 입력 칸 없음) → 고유 대상 등록
//   → API: 공급자 CLOUD 등록 거절
//   → API로 각 결과 확인: LOCAL·고정 모델·MockFlag false·v1·Accepted false·입력 해시 일치
//     ·Maintenance(demo-temperature-v1) 위험도 = 결과 위험도, Mock 결과 섞임 없음
//   → 분석 이력에서 해당 작업 상세 열기: LOCAL AI·시연 규칙 판정·AI 설명 비신뢰 표시·점검 근거 목록
//     ·참고용·Mock 아님, 결과 없는 작업은 빈 결과 안내, "로컬 AI 1건" 표시, 390px 넘침 없음, 캡처.
// 정리: 등록한 시험 AI 대상만 남는다(이력 보존, 추론 요청 없음). 유료·클라우드 호출 없음.
// 실행: GPT 통합 시험이 local-ai-verification.json을 남기고 Web을 배포한 뒤
// `node verify-analyses-local.cjs`. Mock 화면 회귀는 verify-analyses.cjs가 맡는다.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'AIL-' + crypto.randomUUID().slice(0, 8).toUpperCase();

// 설비 수집 연결과 AI 대상은 같은 대상 코드 공간을 쓰므로 ID를 나눈다.
const aiKey = key + 'AI';
const localModel = 'qwen2.5:1.5b';
const policy = 'demo-temperature-v1';
const reportPath = path.join(output, 'local-ai-verification.json');

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

async function noPageOverflow(page, label) {
  const extra = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  if (extra > 1) {
    throw new Error(`${label}: page is ${extra}px wider than viewport`);
  }
}

// 작업 조회: 200이 아니면 null을 돌려 "완료"로 오인하지 않게 한다.
async function getJob(page, jobId) {
  const result = await api(page, 'GET', '/api/v1/analyses/' + encodeURIComponent(jobId));
  if (result.status !== 200 || !result.json || !Array.isArray(result.json.results)) {
    console.log(`JOB ${jobId} HTTP ${result.status}`);
    return null;
  }
  return result.json;
}

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

  try {
    admin = await login(browser, 'admin', 'admin_password', desktop);

    // 0. GPT 통합 시험 보고서(실제 GPU 결과의 작업 ID)
    if (!fs.existsSync(reportPath)) {
      throw new Error('Run GPT LocalAiVerification first: missing ' + reportPath);
    }
    const report = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
    if (!report.jobId || !report.unknownJobId || report.provider !== 'LOCAL') {
      throw new Error('Local AI report lacks LOCAL job ids: ' + JSON.stringify(report));
    }
    check('GPT local AI report provides real GPU job ids');

    // 1. 화면: 안내와 LOCAL 대상 등록(주소·키·모델 입력 칸 없음)
    await admin.goto(base + '/analyses');
    await admin.getByRole('heading', {
      name: '설비 분석(Mock·로컬 AI)',
      exact: true
    }).waitFor();
    await status(admin, '완료했습니다.');
    await admin.locator('.mock-notice').filter({ hasText: '로컬 AI 분석은' }).waitFor();

    await button(admin.locator('main .toolbar'), '대상 등록').click();
    const targetForm = admin.locator('form.target-create');
    await targetForm.waitFor();
    await targetForm.getByLabel('공급자').selectOption('LOCAL');
    await targetForm.locator('.hint').filter({ hasText: localModel }).waitFor();
    const inputs = await targetForm.locator('input').count();
    if (inputs !== 2) {
      throw new Error('LOCAL target form must only ask ID and name, inputs: ' + inputs);
    }
    await targetForm.getByLabel('대상 ID').fill(aiKey);
    await targetForm.getByLabel('대상 이름').fill('로컬 AI 화면 검증 ' + aiKey);
    const created = admin.waitForResponse(response =>
      new URL(response.url()).pathname === '/api/v1/ai-targets'
        && response.request().method() === 'POST');
    await button(targetForm, '로컬 AI 대상 등록').click();
    const createdResponse = await created;
    const createdBody = createdResponse.request().postDataJSON();
    await status(admin, '로컬 AI 분석 대상을 등록했습니다.');
    if (createdResponse.status() !== 201 || createdBody.providerKind !== 'LOCAL'
      || Object.keys(createdBody).sort().join(',') !== 'displayName,providerKind,targetId') {
      throw new Error('LOCAL target registration body/status: ' + createdResponse.status()
        + ' ' + JSON.stringify(createdBody));
    }
    await admin.getByRole('tab', {
      name: '분석 대상',
      exact: true
    }).click();
    await admin.locator('section.ai-targets tbody tr')
      .filter({ hasText: aiKey })
      .filter({ hasText: 'LOCAL · ' + localModel })
      .waitFor();
    check('Admin registers a LOCAL target with fixed model only (no endpoint/key/model inputs)');

    // 2. CLOUD 공급자는 등록할 수 없다.
    const cloud = await api(admin, 'POST', '/api/v1/ai-targets', {
      targetId: aiKey + 'C',
      displayName: 'CLOUD 거절 확인',
      providerKind: 'CLOUD'
    });
    if (cloud.status < 400 || cloud.status >= 500) {
      throw new Error('CLOUD provider must be refused, got HTTP ' + cloud.status);
    }
    check(`CLOUD provider registration is refused (HTTP ${cloud.status})`);

    // 3. API로 실제 결과 확인(HIGH 등 report.risk, UNKNOWN)
    const cases = [
      {
        jobId: report.jobId,
        risk: report.risk,
        capture: 'FactoryMES-analyses-local-screen.png'
      },
      {
        jobId: report.unknownJobId,
        risk: 'UNKNOWN',
        capture: 'FactoryMES-analyses-local-unknown.png'
      }
    ];
    for (const item of cases) {
      const job = await getJob(admin, item.jobId);
      if (!job || job.results.some(x => x.mockFlag || x.provider === 'MOCK')) {
        throw new Error('LOCAL job missing or contains a Mock result: ' + item.jobId);
      }
      if (job.results.length !== 1) {
        throw new Error(`LOCAL job ${item.jobId} ended ${job.state} with `
          + `${job.results.length} results`);
      }
      const result = job.results[0];
      const evidence = result.evidence;
      const maintenance = evidence.maintenance;
      if (result.provider !== 'LOCAL' || result.model !== localModel || result.mockFlag
        || result.resultVersion !== 1 || result.accepted !== false
        || evidence.inputHash !== job.snapshot.inputHash
        || typeof evidence.summary !== 'string' || evidence.summary.trim() === '') {
        throw new Error('LOCAL result unexpected: ' + JSON.stringify(result).slice(0, 600));
      }
      if (!maintenance || maintenance.policyVersion !== policy
        || maintenance.risk !== result.risk || result.risk !== item.risk
        || !Array.isArray(maintenance.findings) || maintenance.findings.length < 1
        || !Array.isArray(maintenance.actions) || maintenance.actions.length < 1) {
        throw new Error(`Maintenance evidence for ${item.jobId} (expected ${item.risk}): `
          + JSON.stringify({
            risk: result.risk,
            maintenance
          }));
      }
      item.lines = maintenance.findings.length + maintenance.actions.length;
    }
    check(`LOCAL results: fixed model, not Mock, v1, not accepted, hash bound; `
      + `rule risk ${report.risk} and UNKNOWN match maintenance evidence`);

    // 4. 화면: 분석 이력에서 각 작업 상세를 연다(목록에 없으면 더 보기).
    await admin.getByRole('tab', {
      name: '분석 이력',
      exact: true
    }).click();
    const jobs = admin.locator('section.analysis-jobs');
    const detail = admin.locator('section.analysis-detail');
    async function openJob(id) {
      const row = jobs.locator('tbody tr').filter({ hasText: '#' + id });
      for (let page = 0; page < 20 && await row.count() === 0; page++) {
        const more = button(jobs, '분석 이력 더 보기');
        if (await more.count() === 0) {
          break;
        }
        await more.click();
        await status(admin, '완료했습니다.');
      }
      if (await row.count() === 0) {
        throw new Error('Job not found in analysis list: ' + id);
      }
      await button(row, '분석 상세').click();
      await detail.locator('h3').filter({ hasText: '#' + id }).waitFor();
      return row;
    }

    for (const item of cases) {
      const row = await openJob(item.jobId);
      const article = detail.locator('article.result-local').first();
      await article.getByText('LOCAL AI', { exact: true }).waitFor();
      await article.getByText(`위험도 ${item.risk} · 시연 규칙 판정`).waitFor();
      await article.getByText('참고용 · 채택 안 함').waitFor();
      await article.locator('.untrusted-label').filter({ hasText: '비신뢰 참고' }).waitFor();
      const block = article.locator('section.maintenance');
      await block.locator('h5').filter({ hasText: policy }).waitFor();
      if (await block.locator('li').count() !== item.lines) {
        throw new Error(`Maintenance list items differ from evidence for ${item.jobId}`);
      }
      await article.locator('dd').filter({ hasText: '아니오 · 로컬 GPU 모델 실행 결과' })
        .waitFor();
      await admin.screenshot({
        path: path.join(output, item.capture),
        fullPage: true
      });
      await button(detail, '닫기').click();
      await detail.waitFor({ state: 'detached' });
      await row.filter({ hasText: '로컬 AI 1건' }).waitFor();
    }
    check(`Detail shows LOCAL AI, rule risk (${report.risk}/UNKNOWN), untrusted summary, `
      + 'maintenance evidence; list marks LOCAL AI');

    // 5. 자료 없는 작업: 결과 없음 안내(추론하지 않음)
    if (report.noDataJobId) {
      await openJob(report.noDataJobId);
      await detail.locator('.badge').filter({ hasText: 'INSUFFICIENT_DATA' }).waitFor();
      await detail.locator('.empty-note').waitFor();
      if (await detail.locator('article.result').count() !== 0) {
        throw new Error('INSUFFICIENT_DATA job must show no result');
      }
      await button(detail, '닫기').click();
      await detail.waitFor({ state: 'detached' });
      check('No-data job shows INSUFFICIENT_DATA with no result');
    }

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

    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-analyses-local-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          runtimeErrors: errors,
          testKey: key,
          aiTargetKey: aiKey,
          jobIds: [
            report.jobId,
            report.unknownJobId,
            report.noDataJobId ?? null
          ],
          ruleRisks: [
            report.risk,
            'UNKNOWN'
          ],
          provider: 'LOCAL',
          model: localModel,
          newInferenceRequests: 0,
          cloudApiCalls: 0,
          notCovered: 'Inference itself, GPU residency and connector failure codes are '
            + 'verified by GPT LocalAiVerification; this script checks the screen.',
          note: 'Reads existing LOCAL results; only a test AI target is added. '
            + 'Model output, secrets and cookies not saved.'
        },
        null,
        2
      )
    );
    console.log('Browser LOCAL analyses verified');
  } catch (error) {
    console.error(error.message);
    console.error('REPORT ' + reportPath);
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
    // 수집 연결·추론 요청을 만들지 않으므로 정리할 것이 없다(시험 AI 대상은 이력으로 남긴다).
    await browser.close();
  }
})();
