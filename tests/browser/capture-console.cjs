// 제조 콘솔 실제 화면 캡처(읽기 전용).
// 기존 계정으로 로그인해 화면을 열고 보이는 그대로(viewport, fullPage 아님) PNG만 저장한다.
// 자료를 만들거나 바꾸지 않고(GET 화면 조회만), 외부 주소 요청은 막고, 비밀번호는 출력하지 않는다.
//   - 관리자 작업지시 화면(1360×900)
//   - 관리자 경보 화면 첫 화면(1360×900)과 규칙 설정 표
//   - 작업자 작업지시 화면(390×844)
// 실행: GPT 배포 뒤 `node capture-console.cjs`.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const errors = [];

async function login(browser, user, secret, viewport) {
  const context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport
  });

  // localhost 밖으로 나가는 요청은 막는다.
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
  await page.getByRole('button', {
    name: '로그인',
    exact: true
  }).click();
  await page.locator('#workspace').waitFor({ state: 'visible' });
  return page;
}

// 화면을 열고 첫 조회가 끝날 때까지 기다린다(상태 문구 + 처리 중 해제).
async function open(page, route, heading) {
  await page.goto(base + route);
  await page.getByRole('heading', {
    name: heading,
    exact: true
  }).waitFor();
  await page.waitForFunction(
    () => document.querySelector('main > p[role="status"]')?.textContent.includes('완료했습니다.')
      && !document.querySelector('main > fieldset')?.disabled
  );
}

async function overflow(page) {
  return page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
}

async function capture(page, name) {
  const file = path.join(output, name);
  await page.screenshot({
    path: file,
    fullPage: false
  });
  console.log(`SAVED ${name} (page overflow ${await overflow(page)}px)`);
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });

  try {
    const desktop = {
      width: 1360,
      height: 900
    };
    const admin = await login(browser, 'admin', 'admin_password', desktop);

    await open(admin, '/orders', '작업지시');
    await capture(admin, 'FactoryMES-console-admin-orders.png');

    await open(admin, '/alarms', '경보');
    await capture(admin, 'FactoryMES-console-admin-alarms.png');

    // 규칙 설정 표는 관리자 전용 "경보 규칙" 탭에 있다. 탭을 열고 표 영역만 찍는다.
    await admin.getByRole('tab', {
      name: '경보 규칙',
      exact: true
    }).click();
    const rules = admin.locator('section.rules table');
    await rules.scrollIntoViewIfNeeded();
    await rules.screenshot({ path: path.join(output, 'FactoryMES-console-alarm-rules.png') });
    console.log('SAVED FactoryMES-console-alarm-rules.png');

    const phone = await login(browser, 'portal', 'portal_password', {
      width: 390,
      height: 844
    });
    await open(phone, '/orders', '작업지시');
    await capture(phone, 'FactoryMES-console-phone-orders.png');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }
    console.log('Console captures saved');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
