// 일일 생산 통계(Reports) 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-통계-AI-재처리-동시구현分担-v1.md "1단계 통계".
//   고유 품목·설비 + 작업 A(마감): TOTAL 10, REJECT 3, REJECT 1 → 그 REJECT 1 역분개(순 불량 3)
//   + 작업 B(발행만, 미마감) → 관리자 화면 재집계 요청(사유) → Job 성공 대기
//   → 화면 표: 총량 10 · 불량 3 · 양품 7 · 불량률 30.0%, 막대, 미마감 B 제외
//   → 같은 날 재집계 한 번 더 → 같은 값(가산 없음) → 빈 날짜 안내·미래 날짜 거절
//   → 작업 이력 표, 작업자 메뉴 없음·안내·API 403, 390px 넘침 없음, 캡처.
// 정리: 작업 B만 취소한다. 마감 작업 A·품목·설비 이력은 남긴다.
// 실행: GPT 배포 뒤 `node verify-reporting.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'RPT-' + crypto.randomUUID().slice(0, 8).toUpperCase();

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

// expected는 숫자 하나 또는 허용 목록(예: [200, 201])이다.
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

// 영업일도 호스트 시계가 아니라 서버 UTC(X-Mes-Time) 기준 KST 날짜로 계산한다.
function kstDate(serverUtc, offsetDays = 0) {
  const time = new Date(Date.parse(serverUtc) + 9 * 3600 * 1000 + offsetDays * 86400 * 1000);
  return time.toISOString().slice(0, 10);
}

// 실적 시각은 서버 규칙상 작업 시작(StartedAt) 이후이면서 서버 현재 시각 이전이어야 한다.
// 호스트(Date.now)와 컨테이너 시계가 다를 수 있으므로, start 응답 뒤 서버가 내려 주는
// X-Mes-Time(서버 UTC)을 그대로 쓴다. 기존 시험과 같이 소수 6자리(마이크로초)로 자른다.
async function serverNow(page) {
  const header = await page.evaluate(async () => {
    const response = await fetch('/api/v1/auth/csrf', { cache: 'no-store' });
    return response.headers.get('X-Mes-Time');
  });
  if (!header) {
    throw new Error('Server time header X-Mes-Time is missing');
  }
  return header.replace(/(\.\d{6})\d+Z$/, '$1Z');
}

function entry(kind, qty, reason, occurredAt, reversalOf = null) {
  return {
    clientEntryId: crypto.randomUUID(),
    kind,
    qty,
    reversalOf,
    correctionId: null,
    reason,
    occurredAt
  };
}

// 화면에서 날짜를 고르고 집계를 조회한다.
async function query(page, date) {
  const input = page.locator('section.report-query').getByLabel('영업일');
  await input.fill(date);
  await input.dispatchEvent('change');
  await button(page.locator('section.report-query'), '집계 조회').click();
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
  let pendingOrder = null;
  let today = null;
  let serverUtc = null;

  try {
    admin = await login(browser, 'admin', 'admin_password', desktop);
    serverUtc = await serverNow(admin);
    today = kstDate(serverUtc);
    const worker = await login(browser, 'portal', 'portal_password', desktop);
    const me = await expectStatus(worker, 'GET', '/api/v1/auth/me', undefined, 200, 'Worker me');

    // 0. 권한: 작업자는 메뉴·화면·API 모두 없음
    await worker.goto(base + '/reports');
    await worker.getByText('생산 통계는 관리자만 사용할 수 있습니다.').waitFor();
    // 업무 메뉴(nav.shell-menu)가 그려진 뒤에 관리자 메뉴가 없는지 센다(빈 메뉴로 통과하지 않게).
    await worker.locator('nav.shell-menu a').first().waitFor({ state: 'attached' });
    if (await worker.locator('nav.shell-menu a').filter({ hasText: '생산 통계' }).count()) {
      throw new Error('Worker must not see reports menu');
    }
    const denied = await api(
      worker,
      'GET',
      '/api/v1/reports/daily-production?businessDate=' + today
    );
    if (denied.status !== 403) {
      throw new Error('Worker reports API returned ' + denied.status);
    }
    check('Worker has no reports menu, page, or API (403)');

    // 1. fixture: 고유 품목·설비, 마감 작업 A(순 불량 3)와 미마감 작업 B
    const lines = await expectStatus(
      admin,
      'GET',
      '/api/v1/lines?active=true&limit=100',
      undefined,
      200,
      'Lines'
    );
    const product = await expectStatus(
      admin,
      'POST',
      '/api/v1/products',
      {
        code: key,
        name: '통계 검증 품목 ' + key,
        unit: 'EA',
        active: true
      },
      201,
      'Create product'
    );
    const equipment = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: key,
        name: '통계 검증 설비 ' + key,
        lineId: lines.items.find(x => x.code === 'DEMO-LINE').lineId,
        active: true
      },
      201,
      'Create equipment'
    );

    async function newOrder(label) {
      const now = new Date();
      let order = await expectStatus(
        admin,
        'POST',
        '/api/v1/orders',
        {
          productId: product.productId,
          equipmentId: equipment.equipmentId,
          assigneeId: me.userId,
          operationLabel: label,
          targetQty: 10,
          countMode: 'MANUAL',
          counterMappingId: null,
          plannedStart: now.toISOString(),
          plannedEnd: new Date(now.getTime() + 3600000).toISOString(),
          checklist: []
        },
        201,
        'Create order ' + label
      );
      order = await expectStatus(
        admin,
        'POST',
        `/api/v1/orders/${order.orderId}/release`,
        { expectedVersion: order.version },
        200,
        'Release ' + label
      );
      return order;
    }

    let closed = await newOrder('통계 검증 마감 ' + key);
    closed = await expectStatus(
      worker,
      'POST',
      `/api/v1/orders/${closed.orderId}/start`,
      { expectedVersion: closed.version },
      [200, 201],
      'Start order A'
    );
    const firstAt = await serverNow(worker);
    const first = await expectStatus(
      worker,
      'POST',
      `/api/v1/orders/${closed.orderId}/entries`,
      {
        expectedVersion: closed.version,
        entries: [
          entry('TOTAL', 10, null, firstAt),
          entry('REJECT', 3, '외관 불량', firstAt),
          entry('REJECT', 1, '잘못 입력', firstAt)
        ]
      },
      [200, 201],
      'Record entries A'
    );
    const wrong = first.entries.find(x => x.kind === 'REJECT' && x.qty === 1);
    const reverseAt = await serverNow(worker);
    const reversed = await expectStatus(
      worker,
      'POST',
      `/api/v1/orders/${closed.orderId}/entries`,
      {
        expectedVersion: first.version,
        entries: [
          entry('REVERSAL', 1, '불량 1 오입력 취소', reverseAt, wrong.entryId)
        ]
      },
      [200, 201],
      'Reverse wrong reject'
    );
    if (reversed.totalQty !== 10 || reversed.rejectQty !== 3) {
      throw new Error('Order A net quantities unexpected: ' + JSON.stringify(reversed));
    }
    const submitted = await expectStatus(
      worker,
      'POST',
      `/api/v1/orders/${closed.orderId}/submit`,
      {
        expectedVersion: reversed.version,
        completionReason: null
      },
      [200, 201],
      'Submit order A'
    );
    await expectStatus(
      admin,
      'POST',
      `/api/v1/orders/${closed.orderId}/close`,
      {
        expectedVersion: submitted.version,
        reason: '통계 검증 마감'
      },
      [200, 201],
      'Close order A'
    );
    pendingOrder = await newOrder('통계 검증 미마감 ' + key);
    check('Fixture: closed order (10 total, net 3 reject after reversal) and an open order');

    // 2. 관리자 화면: 오늘 날짜 재집계 요청(사유 필수) → Job 성공 대기
    await admin.goto(base + '/reports');
    await admin.getByRole('heading', {
      name: '생산 통계',
      exact: true
    }).waitFor();
    await status(admin, '완료했습니다.');
    await noPageOverflow(admin, 'Admin reports desktop');

    async function rebuild(reason) {
      await query(admin, today);
      await status(admin, '완료했습니다.');
      await button(admin.locator('section.report-query'), '재집계 요청').click();
      const dialog = admin.locator('form.rebuild-dialog');
      await dialog.waitFor();
      await dialog.getByLabel('재집계 사유').fill(reason);
      await button(dialog, '재집계 요청 보내기').click();
      await status(admin, '재집계 요청 #');
      await dialog.waitFor({ state: 'detached' });
      const text = await admin.locator('main > p[role="status"]').textContent();
      const runId = text.match(/#(\d+)/)[1];
      const run = await waitUntil(
        () => api(admin, 'GET', '/api/v1/job-runs/' + runId),
        found => found.status === 200 && found.json.status !== 'RUNNING',
        'Rebuild job did not finish'
      );
      if (run.json.status !== 'SUCCEEDED' || run.json.jobKey !== 'daily-production:' + today) {
        throw new Error('Rebuild job result unexpected: ' + JSON.stringify(run.json));
      }
      return run.json;
    }

    const firstRun = await rebuild('통계 검증 재집계 ' + key);
    check('Admin requests rebuild with reason in popup; job succeeds for today');

    // 3. 화면 표: 우리 설비·품목 행이 10 / 3 / 7 / 30.0%, 막대 있음, 미마감 작업 B 제외
    async function ourRow() {
      await query(admin, today);
      await status(admin, '완료했습니다.');
      let row = admin.locator('section.report-result tbody tr').filter({ hasText: key });
      while (!(await row.count())
        && await button(admin.locator('section.report-result'), '실적 더 보기').count()) {
        await button(admin.locator('section.report-result'), '실적 더 보기').click();
        await status(admin, '완료했습니다.');
        row = admin.locator('section.report-result tbody tr').filter({ hasText: key });
      }
      await row.first().waitFor();
      return (await row.first().locator('td').allTextContents()).map(x => x.trim());
    }

    const cells = await ourRow();
    if (cells[2] !== '10' || cells[3] !== '3' || cells[4] !== '7' || cells[5] !== '30.0%') {
      throw new Error('Report row unexpected: ' + JSON.stringify(cells));
    }
    await admin.locator('section.report-result tbody tr')
      .filter({ hasText: key })
      .locator('.bar .bar-reject')
      .waitFor();
    await admin.locator('section.report-status .badge').filter({ hasText: '집계 완료' }).waitFor();
    const apiRows = await expectStatus(
      admin,
      'GET',
      `/api/v1/reports/daily-production?businessDate=${today}&limit=100`,
      undefined,
      200,
      'Report rows'
    );
    const ours = apiRows.items.filter(x => x.equipmentId === equipment.equipmentId);
    if (ours.length !== 1 || ours[0].totalQty !== 10 || ours[0].rejectQty !== 3) {
      throw new Error('API rows unexpected (open order must be excluded): ' + JSON.stringify(ours));
    }
    check('Report shows 10 total / 3 reject / 7 good / 30.0% with bar; open order excluded');

    // 4. 같은 날 재집계 한 번 더 → 같은 값(가산 아님)
    await rebuild('통계 검증 재실행 ' + key);
    const again = await ourRow();
    if (again[2] !== '10' || again[3] !== '3') {
      throw new Error('Rebuild doubled or changed values: ' + JSON.stringify(again));
    }
    check('Second rebuild recomputes the same values (no double counting)');

    // 5. 빈 날짜는 0 실적과 구분해 안내, 미래 날짜는 화면에서 거절
    await query(admin, '2000-01-01');
    await status(admin, '완료했습니다.');
    const badge = (await admin.locator('section.report-status .badge').textContent()).trim();
    const note = (await admin.locator('section.report-result .empty-note').textContent()).trim();
    const distinct = (badge === '미집계' && note.includes('아직 집계하지 않아'))
      || (badge === '집계 완료' && note.includes('(0)'));
    if (!distinct) {
      throw new Error(`Empty date state unclear: ${badge} / ${note}`);
    }
    await query(admin, kstDate(serverUtc, 1));
    await status(admin, '영업일은 2000-01-01부터 오늘');
    check('Empty date is labeled (not-yet-built vs built-zero); future date refused in UI');

    // 6. 작업 이력 표에 방금 요청한 작업이 보인다("작업 이력" 탭)
    await admin.getByRole('tab', {
      name: '작업 이력',
      exact: true
    }).click();
    await button(admin.locator('section.job-runs'), '작업 이력 새로고침').click();
    await status(admin, '완료했습니다.');
    await admin.locator('section.job-runs tbody tr')
      .filter({ hasText: '#' + firstRun.jobRunId + ' ' })
      .first()
      .waitFor();
    await query(admin, today);
    await status(admin, '완료했습니다.');
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-reports-screen.png'),
      fullPage: true
    });
    check('Job run history lists the rebuild; desktop capture saved');

    // 7. 390px: 본문 넘침 없음
    const phone = await login(browser, 'admin', 'admin_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/reports');
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone reports');
    // 좁은 화면: "메뉴" 버튼으로 업무 메뉴를 열면 관리자에게 생산 통계가 보이고 활성 표시다.
    await phone.locator('header.shell-top').getByRole('button', {
      name: '메뉴',
      exact: true
    }).click();
    const phoneMenu = phone.locator('nav.shell-menu');
    await phoneMenu.waitFor({ state: 'visible' });
    await phoneMenu.getByRole('link', {
      name: '생산 통계',
      exact: true
    }).waitFor();
    if ((await phoneMenu.locator('a.active').textContent()).trim() !== '생산 통계') {
      throw new Error('Reports menu is not marked active on phone');
    }
    check('Phone width 390: no page overflow; reports menu visible for admin');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      testKey: key,
      businessDate: today,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'Closed order, product and equipment kept as history; open order cancelled. '
        + 'Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-reporting-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser reporting verified');
  } catch (error) {
    console.error(error.message);
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
    // 정리: 미마감 작업 B만 취소한다(이력은 남긴다).
    if (admin && pendingOrder) {
      try {
        const current = await api(admin, 'GET', '/api/v1/orders/' + pendingOrder.orderId);
        if (current.status === 200 && current.json.status === 'RELEASED') {
          await api(admin, 'POST', `/api/v1/orders/${pendingOrder.orderId}/cancel`, {
            expectedVersion: current.json.version,
            reason: 'Browser reporting verification complete'
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
