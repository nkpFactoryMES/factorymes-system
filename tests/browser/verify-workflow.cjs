const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const project = path.resolve(__dirname, '../..');
  const output = path.join(project, 'verification-results');
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });
  const errors = [];
  const checks = [];
  const contexts = [];
  const check = (name) => {
    checks.push(name);
    console.log('PASS ' + name);
  };
  async function pageFor(login, secret) {
    const context = await browser.newContext({
      ignoreHTTPSErrors: true,
      viewport: {
        width: 1360,
        height: 1000
      }
    });
    contexts.push(context);
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
    await page.getByRole('button', {
      name: '로그인',
      exact: true
    }).click();
    await page.locator('#workspace').waitFor({ state: 'visible' });
    if (await page.locator('#password').inputValue() !== '') {
      throw new Error('Password field was not cleared');
    }
    check(login + ' browser login and password clearing');
    await page.getByRole('link', { name: '작업지시 업무 화면 열기' }).click();
    await page.getByRole('heading', {
      name: '작업지시',
      exact: true
    }).waitFor();
    await page.getByRole('button', {
      name: '새로고침',
      exact: true
    }).waitFor();
    await page.waitForFunction(() =>
      document.querySelector('[role="status"]')?.textContent === '완료했습니다.'
    );
    const styled = await page.locator('main').evaluate(element => {
      const sheet = Array.from(document.styleSheets).find(style =>
        style.href && new URL(style.href).pathname === '/factorymes.css'
      );
      // 색 띠는 업무 shell 상단(header.shell-top)에 있다. 본문 머리글은 밝은 제목줄이다.
      const header = document.querySelector('header.shell-top');
      return sheet?.cssRules.length > 0
        && getComputedStyle(element).maxWidth !== 'none'
        && header
        && getComputedStyle(header).backgroundColor !== 'rgba(0, 0, 0, 0)';
    });
    if (!styled) {
      throw new Error('Shared application theme was not loaded');
    }
    check(login + ' WebAssembly page initialized with shared application theme');
    return page;
  }
  async function finished(page) {
    await page.waitForFunction(() =>
      document.querySelector('[role="status"]')?.textContent === '완료했습니다.'
      && !document.querySelector('main > fieldset')?.disabled
    );
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
  const workerLogin = process.env.MES_BROWSER_WORKER_LOGIN ?? 'worker.demo';
  const workerSecret = process.env.MES_BROWSER_WORKER_SECRET ?? 'worker_password';
  const title = '브라우저 시연 ' + Date.now();
  try {
    const admin = await pageFor('admin', 'admin_password');
    // 작업지시 작성은 상단 버튼으로 여는 팝업이다(목록 아래 상시 폼 없음).
    await admin.getByRole('button', {
      name: '작업지시 작성',
      exact: true
    }).click();
    await admin.locator('section.order-create').waitFor();
    await admin.getByLabel('품목', { exact: false }).selectOption({ label: '시연 부품 (DEMO-EA)' });
    await admin.locator('section.order-create form > label').filter({ hasText: /^\s*설비/ }).locator('select').selectOption({ label: '시연 수동 조립 설비' });
    await admin.getByLabel('담당 작업자').selectOption({ label: `${workerLogin} (${workerLogin})` });
    await admin.getByLabel('작업명', { exact: true }).fill(title);
    await admin.getByLabel('목표 수량 (EA)').fill('10');
    await admin.getByRole('button', {
      name: '초안 저장',
      exact: true
    }).click();
    await admin.getByRole('heading', { name: '작업 상세 · ' + title }).waitFor();
    check('Admin creates draft in WebAssembly');
    await detailTab(admin, '기본');
    await admin.getByRole('button', {
      name: '작업지시 발행',
      exact: true
    }).click();
    await finished(admin);
    check('Admin releases draft in browser');
    const worker = await pageFor(workerLogin, workerSecret);
    if (await worker.getByRole('heading', { name: '작업지시 작성' }).count()) {
      throw new Error('Worker unexpectedly has admin create UI');
    }
    await worker.getByRole('row').filter({ hasText: title })
      .getByRole('button', {
        name: '상세',
        exact: true
      }).click();
    await detailTab(worker, '기본');
    await worker.getByRole('checkbox').check();
    await finished(worker);
    await worker.getByRole('button', {
      name: '작업 시작',
      exact: true
    }).click();
    await detailTab(worker, '실적·제출');
    await worker.getByRole('heading', { name: '실적 추가 (기존 합계에 더해집니다)' }).waitFor();
    check('Assigned worker checks and starts');
    await worker.getByLabel('총수량 증가 (EA)').fill('10');
    await worker.getByLabel('불량 증가 (EA)').fill('50');
    await worker.getByLabel('불량 사유', { exact: true }).fill('거절 검증');
    await worker.getByRole('button', {
      name: '실적 저장',
      exact: true
    }).click();
    await worker.waitForFunction(() =>
      document.querySelector('[role="status"]')?.textContent.includes('불량 수량')
    );
    if (await worker.getByLabel('불량 증가 (EA)').isDisabled()) {
      throw new Error('Rejected entries must allow editing');
    }
    check('Definitive rejected entry allows input correction');
    await worker.getByLabel('불량 증가 (EA)').fill('2');
    await worker.getByLabel('불량 사유', { exact: true }).fill('외관 검사 불량');
    await worker.getByRole('button', {
      name: '실적 저장',
      exact: true
    }).click();
    await finished(worker);
    await worker.getByText('양품 8 · 제출 사유:', { exact: false }).waitFor();
    check('Worker saves total 10 reject 2 good 8');
    await worker.getByLabel('제출 사유 (목표 수량과 다르면 필수)').fill('시연 작업 완료');
    await worker.getByRole('button', {
      name: '제출',
      exact: true
    }).click();
    await finished(worker);
    // 제출하면 실적·제출 탭이 없어진다. 마감 버튼이 있을 자리(기본 탭)에서 확인한다.
    await detailTab(worker, '기본');
    if (await worker.getByRole('button', {
      name: '관리자 마감',
      exact: true
    }).count()) {
      throw new Error('Worker unexpectedly has close UI');
    }
    check('Worker submits and has no close button');
    await admin.getByRole('button', {
      name: '새로고침',
      exact: true
    }).click();
    await detailTab(admin, '기본');
    await admin.getByLabel('마감 검토 사유').waitFor();
    await admin.getByLabel('마감 검토 사유').fill('총수량·불량 및 필수 점검 확인');
    await admin.getByRole('button', {
      name: '관리자 마감',
      exact: true
    }).click();
    await finished(admin);
    const detail = admin.locator('section').filter({
      has: admin.getByRole('heading', { name: '작업 상세 · ' + title })
    });
    if (!(await detail.innerText()).includes('CLOSED')) {
      throw new Error('Order did not reach CLOSED');
    }
    check('Admin closes; CLOSED persisted and shown');
    await admin.screenshot({
      path: path.join(output,
      'FactoryMES-manual-closed.png'),
      fullPage: true
    });
    // 업무 화면 머리띠의 '로그아웃' 한 번으로 로그아웃하고 로그인 화면으로 돌아간다.
    await worker.locator('header.shell-top').getByRole('button', {
      name: '로그아웃',
      exact: true
    }).click();
    await worker.locator('#login-form').waitFor({ state: 'visible' });
    const session = await worker.evaluate(
      async () => (await fetch('/api/v1/auth/me', { cache: 'no-store' })).status
    );
    if (session !== 401) {
      throw new Error('Session still active after header logout: ' + session);
    }
    check('Browser logout returns to login');
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
      note: 'One CLOSED demo order retained intentionally; secrets and cookies not saved.'
    };
    fs.writeFileSync(path.join(output, 'FactoryMES-browser-workflow-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      ));
    console.log('Browser workflow verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        console.error('PAGE STATUS: ' + await page.locator('[role="status"]').textContent().catch(() => 'none'));
      }
    }
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();

