// 제출 전(RUNNING·PAUSED) 작업자 실적 취소(역분개)를 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-제출전역분개-동시구현분담-v1.md의 "Claude 검증" 시나리오.
// 실행: GPT가 Backend·부모 연결과 컨테이너 배포를 마친 뒤 `node verify-pre-submit-reversal.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
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

function button(scope, name) {
  return scope.getByRole('button', {
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

async function parentIdle(page) {
  await page.waitForFunction(() =>
    !document.querySelector('main > fieldset')?.disabled
  );
}

async function login(browser, user, secret) {
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
  await page.locator('#login').fill(user);
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

// 부모 상세의 "목표 N / 총수량 T / 불량 R" 문구가 기대값이 될 때까지 기다린다.
async function totals(page, total, reject) {
  await page.getByText(`총수량 ${total} / 불량 ${reject}`, { exact: false }).waitFor();
}

async function cancelSelected(page, pattern, reason) {
  const section = page.locator('section.pre-submit-reversal');
  await section.getByRole('checkbox', { name: pattern }).first().check();
  await section.getByLabel('취소 사유').fill(reason);
  await button(section, '선택 실적 취소').click();
}

async function addEntries(page, total, reject, reason) {
  await page.getByLabel('총수량 증가 (EA)').fill(String(total));
  await page.getByLabel('불량 증가 (EA)').fill(String(reject));
  await page.getByLabel('불량 사유', { exact: true }).fill(reason);
  await button(page, '실적 저장').click();
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });
  const title = '제출 전 취소 검증 ' + crypto.randomUUID().slice(0, 8);

  try {
    // 1. 관리자 발행 → 작업자 점검·시작
    const admin = await login(browser, adminLogin, adminSecret);
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

    const worker = await login(browser, workerLogin, workerSecret);
    await openOrder(worker, title);
    await detailTab(worker, '기본');
    await worker.getByRole('checkbox').first().check();
    await parentIdle(worker);
    await button(worker, '작업 시작').click();
    await detailTab(worker, '실적·제출');
    await worker.locator('section.pre-submit-reversal').waitFor();
    check('Worker starts; pre-submit reversal section shown');

    // 2. 10/2 입력 → REJECT 2 취소 → 불량 0
    await addEntries(worker, 10, 2, '외관 불량');
    await totals(worker, 10, 2);
    await cancelSelected(worker, /REJECT 2 EA/, '불량 오입력');
    await totals(worker, 10, 0);
    check('Worker cancels REJECT 2 while RUNNING; totals 10/0');

    // 3. 같은 원본은 다시 고를 수 없다
    const rejectLeft = await worker.locator('section.pre-submit-reversal')
      .getByRole('checkbox', { name: /REJECT 2 EA/ })
      .count();
    if (rejectLeft !== 0) {
      throw new Error('Reversed original must not be selectable again');
    }
    check('Reversed original is no longer selectable');

    // 4. 관리자 화면에는 취소 폼이 없다
    await openOrder(admin, title);
    if (await admin.locator('section.pre-submit-reversal').count()) {
      throw new Error('Admin must not see the pre-submit reversal form');
    }
    check('Admin has no pre-submit reversal form');

    // 5. 중지 → PAUSED에서 TOTAL 10 취소 → 0/0
    await detailTab(worker, '중지·정정');
    await worker.getByLabel('중지 사유').selectOption('MATERIAL');
    await worker.getByLabel('메모', { exact: false }).fill('자재 대기');
    await button(worker, '작업 중지').click();
    await button(worker, '작업 재개').waitFor();
    await detailTab(worker, '실적·제출');
    await worker.locator('section.pre-submit-reversal').waitFor();
    await cancelSelected(worker, /TOTAL 10 EA/, '총수량 오입력');
    await totals(worker, 0, 0);
    check('Worker cancels TOTAL 10 while PAUSED; totals 0/0');

    // 6. 재개 → 다시 10/2 입력 → 제출하면 취소 폼이 사라진다
    await detailTab(worker, '중지·정정');
    await button(worker, '작업 재개').click();
    await button(worker, '작업 중지').waitFor();
    await detailTab(worker, '실적·제출');
    await addEntries(worker, 10, 2, '외관 불량');
    await totals(worker, 10, 2);
    await worker.getByLabel('제출 사유 (목표 수량과 다르면 필수)').fill('제출 전 취소 검증');
    await button(worker, '제출').click();
    await parentIdle(worker);
    await worker.locator('section.pre-submit-reversal').waitFor({ state: 'detached' });
    check('Worker re-enters 10/2 and submits; reversal form gone after SUBMITTED');

    // 7. 기존 관리자 정정 흐름 유지: 요청 → 작업자 정정 → 확인 → 마감
    await openOrder(admin, title);
    await detailTab(admin, '중지·정정');
    await admin.getByLabel('정정 요청 사유').fill('불량 수량 재확인');
    await button(admin, '정정 요청').click();
    await admin.locator('section.operations tbody tr')
      .filter({ hasText: 'OPEN' })
      .first()
      .waitFor();

    await openOrder(worker, title);
    await detailTab(worker, '중지·정정');
    const operations = worker.locator('section.operations');
    const select = operations.getByLabel('취소할 원본 실적 (선택)');
    const original = await select.locator('option')
      .filter({ hasText: 'REJECT 2 EA' })
      .first()
      .getAttribute('value');
    await select.selectOption(original);
    await operations.getByLabel('추가 불량 (EA)').fill('1');
    await operations.getByLabel('정정 사유').fill('재검사 결과 불량 1');
    await button(operations, '정정 실적 저장').click();
    await totals(worker, 10, 1);

    await openOrder(admin, title);
    await detailTab(admin, '중지·정정');
    await admin.getByLabel('확인 메모', { exact: false }).fill('불량 1 확인');
    await button(admin, '정정 확인 완료').click();
    await admin.locator('section.operations tbody tr')
      .filter({ hasText: 'RESOLVED' })
      .first()
      .waitFor();
    await detailTab(admin, '기본');
    await admin.getByLabel('마감 검토 사유').fill('제출 전 취소·정정 반영 후 마감');
    await button(admin, '관리자 마감').click();
    await parentIdle(admin);
    await admin.getByText('CLOSED', { exact: false }).first().waitFor();
    check('Existing admin correction flow still works; order CLOSED');

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
      note: 'One CLOSED order retained intentionally; secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-pre-submit-reversal-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser pre-submit reversal verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const parent = await page.locator('main > p[role="status"]')
          .textContent()
          .catch(() => 'none');
        const reversal = await page.locator('section.pre-submit-reversal [role="status"]')
          .textContent()
          .catch(() => 'none');
        console.error(`PAGE STATUS: ${parent} | REVERSAL STATUS: ${reversal}`);
      }
    }
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
