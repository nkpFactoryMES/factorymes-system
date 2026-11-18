// 작업 중지·재개와 마감 전 실적 정정을 실제 브라우저(Edge headless)로 확인한다.
// 기존 verify-workflow.cjs와 같은 방식(로컬 출처만 허용, 비밀번호는 .secrets에서만 읽기)을 따른다.
// 실행: GPT가 Backend·부모 화면 통합과 컨테이너 배포를 마친 뒤 `node verify-lifecycle.cjs`.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const adminLogin = process.env.MES_BROWSER_ADMIN_LOGIN ?? 'admin';
const adminSecret = process.env.MES_BROWSER_ADMIN_SECRET ?? 'admin_password';
const workerLogin = process.env.MES_BROWSER_WORKER_LOGIN ?? 'portal';
const workerSecret = process.env.MES_BROWSER_WORKER_SECRET ?? 'portal_password';

const checks = [];
const errors = [];
const contexts = [];

function check(name) {
  checks.push(name);
  console.log('PASS ' + name);
}

function button(page, name) {
  return page.getByRole('button', {
    name,
    exact: true
  });
}

// 작업 상세 안 탭(기본 / 실적·제출 / 중지·정정 / 초안·취소)을 고른다. 숨긴 탭의 입력은 조작할 수 없다.
async function detailTab(page, name) {
  await page.getByRole('tablist', { name: '작업 상세' })
    .getByRole('tab', {
      name,
      exact: true
    })
    .click();
}

// 부모 화면의 처리 표시가 끝날 때까지 기다린다.
async function parentIdle(page) {
  await page.waitForFunction(() =>
    !document.querySelector('main > fieldset')?.disabled
  );
}

// 정정 이력 표에서 해당 상태의 행이 실제로 나타날 때까지 기다린다.
// 버튼의 보임·숨김은 조회 중에 잠깐 바뀔 수 있어 완료 신호로 쓰지 않는다.
async function correctionRow(page, status) {
  await page.locator('section.operations tbody tr')
    .filter({ hasText: status })
    .first()
    .waitFor();
}

async function pageFor(browser, login, secret) {
  const context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport: {
      width: 1360,
      height: 1000
    }
  });
  contexts.push(context);

  // 테스트 브라우저는 localhost 밖으로 요청하지 않는다.
  await context.route('**/*', route => {
    const hostname = new URL(route.request().url()).hostname;
    return hostname === 'localhost' ? route.continue() : route.abort();
  });

  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));

  await page.goto('https://localhost:7443');
  await page.locator('#login-form').waitFor({ state: 'visible' });
  await page.locator('#login').fill(login);
  await page.locator('#password').fill(
    fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets'), secret), 'utf8').trim()
  );
  await button(page, '로그인').click();
  await page.locator('#workspace').waitFor({ state: 'visible' });

  await page.getByRole('link', { name: '작업지시 업무 화면 열기' }).click();
  await button(page, '새로고침').waitFor();
  await parentIdle(page);
  return page;
}

async function openOrder(page, title) {
  await button(page, '새로고침').click();
  await parentIdle(page);
  await page.getByRole('row')
    .filter({ hasText: title })
    .getByRole('button', {
      name: '상세',
      exact: true
    })
    .click();
  await page.getByRole('heading', { name: '작업 상세 · ' + title }).waitFor();
  await parentIdle(page);
}

async function detailText(page, title) {
  return page.locator('section')
    .filter({ has: page.getByRole('heading', { name: '작업 상세 · ' + title }) })
    .first()
    .innerText();
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });
  const title = '중지·정정 검증 ' + Date.now();

  try {
    // 1. 관리자: 작업지시 작성·발행
    const admin = await pageFor(browser, adminLogin, adminSecret);
    // 작업지시 작성은 상단 버튼으로 여는 팝업이다(목록 아래 상시 폼 없음).
    await admin.getByRole('button', {
      name: '작업지시 작성',
      exact: true
    }).click();
    await admin.locator('section.order-create').waitFor();
    await admin.getByLabel('품목', { exact: false }).selectOption({ label: '시연 부품 (DEMO-EA)' });
    await admin.getByLabel('설비', { exact: false }).selectOption({ label: '시연 수동 조립 설비' });
    await admin.getByLabel('담당 작업자').selectOption({ label: `${workerLogin} (${workerLogin})` });
    await admin.getByLabel('작업명', { exact: true }).fill(title);
    await admin.getByLabel('목표 수량 (EA)').fill('10');
    await button(admin, '초안 저장').click();
    await admin.getByRole('heading', { name: '작업 상세 · ' + title }).waitFor();
    await detailTab(admin, '기본');
    await button(admin, '작업지시 발행').click();
    await parentIdle(admin);
    check('Admin creates and releases order');

    // 2. 작업자: 점검·시작
    const worker = await pageFor(browser, workerLogin, workerSecret);
    await openOrder(worker, title);
    await detailTab(worker, '기본');
    await worker.getByRole('checkbox').check();
    await parentIdle(worker);
    await button(worker, '작업 시작').click();
    await detailTab(worker, '중지·정정');
    await button(worker, '작업 중지').waitFor();
    check('Worker starts and sees pause form');

    // 3. 작업자: 중지 → 관리자는 재개 버튼 없음 → 작업자 재개
    await worker.getByLabel('중지 사유').selectOption('MATERIAL');
    await worker.getByLabel('메모', { exact: false }).fill('자재 대기 검증');

    // 전송 중 전환 회귀: 중지 요청을 브라우저에서 잠시 붙잡아 둔 동안에는
    // 상세 탭과 다른 작업 "상세" 버튼이 모두 잠기고, 보던 탭(중지·정정)이 그대로여야 한다.
    let releasePause;
    const pauseHeld = new Promise(resolve => {
      releasePause = resolve;
    });
    const isPause = url => /\/api\/v1\/orders\/[^/]+\/pause$/.test(url.pathname);
    const holdPause = async route => {
      await pauseHeld;
      await route.continue();
    };
    await worker.route(isPause, holdPause);
    await button(worker, '작업 중지').click();
    await worker.waitForFunction(() => {
      const tabs = Array.from(document.querySelectorAll(
        '[role="tablist"][aria-label="작업 상세"] [role="tab"]'
      ));
      const opens = Array.from(document.querySelectorAll('section.order-list tbody button'));
      return tabs.length > 1
        && tabs.every(tab => tab.disabled)
        && opens.length > 0
        && opens.every(open => open.disabled);
    });
    const heldTab = await worker.locator('[role="tab"][aria-selected="true"]')
      .first()
      .textContent();
    if (heldTab.trim() !== '중지·정정') {
      throw new Error('Selected tab changed while request in flight: ' + heldTab);
    }
    // 잠긴 탭은 눌러도 바뀌지 않는다(force로 잠금 자체를 확인).
    await worker.getByRole('tablist', { name: '작업 상세' })
      .getByRole('tab', {
        name: '기본',
        exact: true
      })
      .click({ force: true });
    if ((await worker.locator('[role="tab"][aria-selected="true"]').first().textContent())
      .trim() !== '중지·정정') {
      throw new Error('Disabled tab switched while request in flight');
    }
    releasePause();
    await button(worker, '작업 재개').waitFor();
    await worker.unroute(isPause, holdPause);
    await worker.waitForFunction(() => Array.from(document.querySelectorAll(
      '[role="tablist"][aria-label="작업 상세"] [role="tab"]'
    )).every(tab => !tab.disabled));
    check('In-flight child request locks detail tabs and order switching; '
      + 'result applies to the same order and tab');
    if (!(await detailText(worker, title)).includes('PAUSED')) {
      throw new Error('Order did not show PAUSED after pause');
    }
    check('Worker pauses with reason; PAUSED shown');

    await openOrder(admin, title);
    await detailTab(admin, '중지·정정');
    if (await button(admin, '작업 재개').count() || await button(admin, '작업 중지').count()) {
      throw new Error('Admin must not have pause/resume controls');
    }
    check('Admin sees paused state without pause/resume controls');

    await button(worker, '작업 재개').click();
    await button(worker, '작업 중지').waitFor();
    check('Worker resumes; RUNNING again');

    // 4. 작업자: 실적 10/2 입력 후 제출
    await detailTab(worker, '실적·제출');
    await worker.getByLabel('총수량 증가 (EA)').fill('10');
    await worker.getByLabel('불량 증가 (EA)').fill('2');
    await worker.getByLabel('불량 사유', { exact: true }).fill('외관 불량');
    await button(worker, '실적 저장').click();
    await worker.getByText('양품 8', { exact: false }).waitFor();
    await worker.getByLabel('제출 사유 (목표 수량과 다르면 필수)').fill('정정 검증용 제출');
    await button(worker, '제출').click();
    await parentIdle(worker);
    check('Worker records 10/2 and submits');

    // 5. 관리자: 정정 요청 → 열린 정정이 있으면 마감 거절
    await openOrder(admin, title);
    await detailTab(admin, '중지·정정');
    await admin.getByLabel('정정 요청 사유').fill('불량 수량 재확인 필요');
    await button(admin, '정정 요청').click();
    await correctionRow(admin, 'OPEN');
    await button(admin, '정정 확인 완료').waitFor();
    check('Admin opens correction; OPEN shown');

    await detailTab(admin, '기본');
    await admin.getByLabel('마감 검토 사유').fill('정정 중 마감 시도');
    await button(admin, '관리자 마감').click();
    await parentIdle(admin);
    if ((await detailText(admin, title)).includes('CLOSED')) {
      throw new Error('Close must be rejected while a correction is OPEN');
    }
    check('Close rejected while correction is OPEN');

    // 6. 작업자: 원본 불량 2를 역분개하고 불량 1로 다시 입력(같은 배치)
    await openOrder(worker, title);
    await detailTab(worker, '중지·정정');
    await worker.getByLabel('정정 사유').waitFor();
    if (await button(worker, '정정 요청').count() || await button(worker, '정정 확인 완료').count()) {
      throw new Error('Worker must not have correction request/resolve controls');
    }

    const select = worker.getByLabel('취소할 원본 실적 (선택)');
    const original = await select.locator('option')
      .filter({ hasText: 'REJECT 2 EA' })
      .getAttribute('value');
    await select.selectOption(original);
    await worker.getByLabel('추가 불량 (EA)').fill('1');
    await worker.getByLabel('정정 사유').fill('불량 재검사 결과 1개');
    await button(worker, '정정 실적 저장').click();
    await worker.getByText('양품 9', { exact: false }).waitFor();
    check('Worker reverses original reject 2 and records reject 1 (good 9)');

    // 7. 관리자: 정정 확인 → 마감
    await openOrder(admin, title);
    await detailTab(admin, '중지·정정');
    await admin.getByLabel('확인 메모', { exact: false }).fill('불량 1개로 정정 확인');
    await button(admin, '정정 확인 완료').click();
    await correctionRow(admin, 'RESOLVED');
    if (await admin.locator('section.operations tbody tr').filter({ hasText: 'OPEN' }).count()) {
      throw new Error('An OPEN correction remained after resolve');
    }
    check('Admin resolves correction; RESOLVED shown');

    await detailTab(admin, '기본');
    await admin.getByLabel('마감 검토 사유').fill('정정 반영 후 마감');
    await button(admin, '관리자 마감').click();
    await parentIdle(admin);
    if (!(await detailText(admin, title)).includes('CLOSED')) {
      throw new Error('Order did not reach CLOSED');
    }
    await detailTab(admin, '중지·정정');
    if (await button(admin, '정정 요청').count()) {
      throw new Error('Closed order must not offer correction');
    }
    check('Admin closes; no correction controls after CLOSED');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      orderLabel: title,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'One CLOSED lifecycle order retained intentionally; secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-lifecycle-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser lifecycle verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const parent = await page.locator('main > p[role="status"]')
          .textContent()
          .catch(() => 'none');
        const operations = await page.locator('section.operations [role="status"]')
          .textContent()
          .catch(() => 'none');
        console.error(`PAGE STATUS: ${parent} | OPERATIONS STATUS: ${operations}`);
      }
    }
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
