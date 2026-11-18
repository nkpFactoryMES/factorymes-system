// 업무 shell(공통 상단·역할별 메뉴·본문 교체)을 실제 브라우저로 확인한다.
//   관리자·작업자(portal) 메뉴 구성 → 대시보드에서 각 메뉴로 이동할 때 문서 유지(본문만 교체)
//   → 활성 메뉴 → 뒤로 가기 → 직접 URL → 새로고침
//   → 경보 규칙 탭·규칙 등록 팝업 → 기준정보 탭·상단 등록 팝업 → 390px 메뉴 접기·열기
// 자료를 만들지 않는다(팝업은 열고 닫기만). 실행: GPT 배포 뒤 `node verify-shell.cjs`.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';

const checks = [];
const errors = [];
const contexts = [];

// 메뉴 이름 → [경로, 본문 제목]
const adminMenu = [
  ['작업지시', '/orders', '작업지시'],
  ['생산 통계', '/reports', '생산 통계'],
  ['경보', '/alarms', '경보'],
  ['설비 연결', '/equipment-connections', '설비 연결·수집'],
  ['설비 분석', '/analyses', '설비 분석(Mock·로컬 AI)'],
  ['알림함', '/inbox', '알림함'],
  ['발송 이력', '/deliveries', '알림 발송 이력'],
  ['기준정보', '/master-data', '기준정보'],
  ['대시보드', '/dashboard', '대시보드']
];
const portalMenu = [
  '대시보드',
  '작업지시',
  '경보',
  '알림함'
];

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

// 본문 제목이 보이고, 본문 입력 묶음(있으면)이 처리 중이 아닐 때까지 기다린다.
async function heading(page, text) {
  await page.getByRole('heading', {
    level: 1,
    name: text,
    exact: true
  }).waitFor();
  await page.waitForFunction(() =>
    !document.querySelector('main > fieldset')?.disabled
  );
}

// 팝업 안에서 키보드로 갈 수 있는 요소(공통 modal.js와 같은 기준).
const FOCUSABLE = 'a[href], button, input, select, textarea, [tabindex]:not([tabindex="-1"])';

// 지금 문서에 표식을 남긴다. 문서를 새로 열면(전체 새로고침) 표식이 사라진다.
async function mark(page) {
  const value = 'shell-' + Date.now();
  await page.evaluate(marker => {
    window.__shellMarker = marker;
  }, value);
  return value;
}

async function marker(page) {
  return page.evaluate(() => window.__shellMarker ?? null);
}

// 활성 메뉴 이름. 좁은 화면에서 접힌 메뉴는 innerText가 비므로 textContent로 읽는다.
async function activeMenu(page) {
  return page.locator('nav.shell-menu a.active').allTextContents();
}

// shell 한 벌·본문 제목 한 개·활성 메뉴 하나가 기대와 같은지 본다.
async function expectShell(page, label, route) {
  // 직접 URL에서는 SSR 제목이 WASM 메뉴 초기화보다 먼저 보일 수 있다.
  // 정적인 제목만이 아니라 실제 활성 메뉴·aria-current가 준비된 뒤 검증한다.
  await page.waitForFunction(({ expectedLabel, expectedRoute }) => {
    const links = Array.from(document.querySelectorAll('nav.shell-menu a.active'));
    return location.pathname === expectedRoute && links.length === 1
      && links[0].textContent.trim() === expectedLabel
      && links[0].getAttribute('aria-current') === 'page';
  }, {
    expectedLabel: label,
    expectedRoute: route
  }, {
    timeout: 15000
  });
  const shape = await page.evaluate(() => ({
    tops: document.querySelectorAll('header.shell-top').length,
    menus: document.querySelectorAll('nav.shell-menu').length,
    titles: document.querySelectorAll('main h1').length,
    path: location.pathname
  }));
  if (shape.tops !== 1 || shape.menus !== 1 || shape.titles !== 1) {
    throw new Error(`${label}: shell shape unexpected ${JSON.stringify(shape)}`);
  }
  if (shape.path !== route) {
    throw new Error(`${label}: path ${shape.path}, expected ${route}`);
  }
  const active = (await activeMenu(page)).map(x => x.trim());
  if (active.length !== 1 || active[0] !== label) {
    throw new Error(`${label}: active menu is ${JSON.stringify(active)}`);
  }
  // 활성 링크의 주소와 aria-current도 현재 화면과 맞아야 한다.
  const link = await page.locator('nav.shell-menu a.active').evaluate(a => ({
    path: new URL(a.href).pathname,
    current: a.getAttribute('aria-current')
  }));
  if (link.path !== route || link.current !== 'page') {
    throw new Error(`${label}: active link ${JSON.stringify(link)}`);
  }
}

async function noPageOverflow(page, label) {
  const extra = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  if (extra > 1) {
    throw new Error(`${label}: page is ${extra}px wider than viewport`);
  }
}

async function selectedTab(page) {
  return (await page.locator('[role="tab"][aria-selected="true"]').allInnerTexts())
    .map(x => x.trim());
}

// 키보드만으로 팝업을 연다·돈다·닫는다.
//   여는 버튼에서 Enter → 팝업 안 첫 입력칸에 포커스 → Tab을 (요소 수 + 2)번 눌러도 팝업 안
//   → 첫 요소에서 Shift+Tab이면 마지막 요소 → Esc로 닫힘 → 여는 버튼으로 포커스 복귀
async function keyboardPopup(page, opener, name) {
  await opener.focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog', { name });
  await dialog.waitFor();
  await page.waitForFunction(
    label => document.activeElement?.closest('[role="dialog"]')?.getAttribute('aria-label')
      === label,
    name
  );
  if (!await page.evaluate(() => document.activeElement.matches('input, select, textarea'))) {
    throw new Error(`${name}: first focus is not an input`);
  }

  const count = await dialog.evaluate((element, focusable) =>
    Array.from(element.querySelectorAll(focusable))
      .filter(item => !item.matches(':disabled') && item.getClientRects().length > 0)
      .length,
  FOCUSABLE);
  for (let index = 0; index < count + 2; index++) {
    await page.keyboard.press('Tab');
    const inside = await dialog.evaluate(element => element.contains(document.activeElement));
    if (!inside) {
      throw new Error(`${name}: Tab ${index + 1} left the popup`);
    }
  }

  const wrapped = await dialog.evaluate((element, focusable) => {
    const list = Array.from(element.querySelectorAll(focusable))
      .filter(item => !item.matches(':disabled') && item.getClientRects().length > 0);
    list[0].focus();
    return list.length;
  }, FOCUSABLE);
  await page.keyboard.press('Shift+Tab');
  const atLast = await dialog.evaluate((element, focusable) => {
    const list = Array.from(element.querySelectorAll(focusable))
      .filter(item => !item.matches(':disabled') && item.getClientRects().length > 0);
    return document.activeElement === list[list.length - 1];
  }, FOCUSABLE);
  if (wrapped < 2 || !atLast) {
    throw new Error(`${name}: Shift+Tab on first item did not wrap to the last`);
  }

  await page.keyboard.press('Escape');
  await dialog.waitFor({ state: 'detached' });
  const handle = await opener.elementHandle();
  await page.waitForFunction(element => document.activeElement === element, handle);
}

// 상단·푸터 위치(px)와 문서 스크롤. 본문만 스크롤되면 앞의 둘은 그대로이고 문서 스크롤은 0이다.
async function shellBoxes(page) {
  return page.evaluate(() => {
    const box = selector => {
      const rect = document.querySelector(selector).getBoundingClientRect();
      return [
        rect.top,
        rect.bottom,
        rect.left,
        rect.width
      ].map(Math.round);
    };
    return {
      top: box('header.shell-top'),
      foot: box('footer.shell-footer'),
      view: window.innerHeight,
      pageScroll: window.scrollY
    };
  });
}

// 본문(.shell-body)을 맨 아래까지 내린 뒤에도 상단·푸터가 같은 자리에 있는지 본다.
async function fixedFrame(page, label) {
  const before = await shellBoxes(page);
  if (before.top[0] !== 0 || before.foot[1] !== before.view) {
    throw new Error(`${label}: header/footer not at viewport edges ${JSON.stringify(before)}`);
  }

  const scrolled = await page.locator('.shell-body').evaluate(body => {
    const room = body.scrollHeight - body.clientHeight;
    body.scrollTop = room;
    return room;
  });
  if (scrolled <= 0) {
    throw new Error(`${label}: body is not long enough to scroll`);
  }

  await page.waitForFunction(() => {
    const body = document.querySelector('.shell-body');
    return body.scrollTop > 0;
  });
  const after = await shellBoxes(page);
  if (JSON.stringify(before) !== JSON.stringify(after)) {
    throw new Error(`${label}: frame moved ${JSON.stringify(before)} -> ${JSON.stringify(after)}`);
  }
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
    height: 900
  };

  try {
    // 1. 관리자: 직접 URL로 shell 대시보드를 연다.
    const admin = await login(browser, 'admin', 'admin_password', desktop);
    await admin.goto(base + '/dashboard');
    await heading(admin, '대시보드');
    await expectShell(admin, '대시보드', '/dashboard');
    const adminLabels = (await admin.locator('nav.shell-menu a').allTextContents())
      .map(x => x.trim());
    const expectedAdmin = ['대시보드', ...adminMenu.slice(0, -1).map(x => x[0])];
    if (JSON.stringify(adminLabels) !== JSON.stringify(expectedAdmin)) {
      throw new Error('Admin menu differs: ' + JSON.stringify(adminLabels));
    }
    check('Admin menu: dashboard plus all business and admin-only menus, grouped');

    // 2. 대시보드 → 각 메뉴 → 대시보드: 문서는 그대로이고 본문·활성 메뉴만 바뀐다.
    const docMarker = await mark(admin);
    for (const [label, route, title] of adminMenu) {
      await admin.locator('nav.shell-menu')
        .getByRole('link', {
          name: label,
          exact: true
        })
        .click();
      await heading(admin, title);
      await expectShell(admin, label, route);
      if (await marker(admin) !== docMarker) {
        throw new Error(`Document reloaded when opening ${label}`);
      }
    }
    check('Dashboard to every menu and back keeps the same document; body and active menu change');

    // 대시보드 본문 링크로도 문서를 다시 열지 않는다.
    await admin.locator('main a.dash-more').filter({ hasText: '경보 화면으로' }).click();
    await heading(admin, '경보');
    await expectShell(admin, '경보', '/alarms');
    if (await marker(admin) !== docMarker) {
      throw new Error('Dashboard panel link reloaded the document');
    }
    check('Dashboard panel link opens alarms inside the shell');

    // 3. 뒤로 가기: 직전 화면(대시보드)으로, 문서 유지
    await admin.goBack();
    await heading(admin, '대시보드');
    await expectShell(admin, '대시보드', '/dashboard');
    if (await marker(admin) !== docMarker) {
      throw new Error('Back navigation reloaded the document');
    }
    await admin.goForward();
    await heading(admin, '경보');
    await expectShell(admin, '경보', '/alarms');
    check('Browser back/forward switch body inside the same document');

    // 4. 직접 URL과 새로고침: 새 문서지만 같은 shell·활성 메뉴로 열린다.
    await admin.goto(base + '/master-data');
    await heading(admin, '기준정보');
    await expectShell(admin, '기준정보', '/master-data');
    if (await marker(admin) !== null) {
      throw new Error('Direct URL should open a new document');
    }
    await admin.reload();
    await heading(admin, '기준정보');
    await expectShell(admin, '기준정보', '/master-data');
    check('Direct URL and refresh open the page inside the shell with active menu');

    // 5. 기준정보: 종류 탭과 상단 "새 X 등록" 팝업(열고 닫기만, 저장하지 않는다)
    const sections = {
      품목: 'section.master-products',
      라인: 'section.master-lines',
      설비: 'section.master-equipment',
      사용자: 'section.master-users'
    };
    for (const [name, selector] of Object.entries(sections)) {
      await admin.getByRole('tab', {
        name,
        exact: true
      }).click();
      await admin.locator(selector).waitFor();
      const others = Object.entries(sections)
        .filter(([other]) => other !== name)
        .map(([, css]) => css)
        .join(', ');
      if (await admin.locator(others).count()) {
        throw new Error(`Master tab ${name} shows other sections`);
      }
      if (JSON.stringify(await selectedTab(admin)) !== JSON.stringify([name])) {
        throw new Error(`Master tab ${name} not selected`);
      }
      await keyboardPopup(
        admin,
        button(admin.locator('main .toolbar'), '새 ' + name + ' 등록'),
        name + ' 등록'
      );
    }
    check('Master data tabs switch sections; top register popups work by keyboard '
      + '(first input focus, Tab trapped, Shift+Tab wraps, Esc closes, focus returns)');

    // 6. 경보: 관리자 탭(경보 목록 / 경보 규칙)과 규칙 등록 팝업
    await admin.locator('nav.shell-menu')
      .getByRole('link', {
        name: '경보',
        exact: true
      })
      .click();
    await heading(admin, '경보');
    await admin.locator('section.alarms').waitFor();
    if (await button(admin.locator('main .toolbar'), '규칙 등록').count()) {
      throw new Error('Rule register button should appear only on the rules tab');
    }
    await admin.getByRole('tab', {
      name: '경보 규칙',
      exact: true
    }).click();
    await admin.locator('section.rules').waitFor();
    if (await admin.locator('section.alarms').count()) {
      throw new Error('Rules tab still shows alarm list');
    }
    await keyboardPopup(
      admin,
      button(admin.locator('main .toolbar'), '규칙 등록'),
      '규칙 등록'
    );
    // 마우스로 연 팝업도 닫기 버튼으로 닫으면 여는 버튼에 포커스가 돌아온다.
    const ruleOpener = button(admin.locator('main .toolbar'), '규칙 등록');
    await ruleOpener.click();
    const ruleDialog = admin.getByRole('dialog', { name: '규칙 등록' });
    await ruleDialog.getByLabel('감시할 연결 대상').waitFor();
    await button(ruleDialog, '닫기').click();
    await ruleDialog.waitFor({ state: 'detached' });
    const ruleHandle = await ruleOpener.elementHandle();
    await admin.waitForFunction(element => document.activeElement === element, ruleHandle);
    await admin.getByRole('tab', {
      name: '경보 목록',
      exact: true
    }).click();
    await admin.locator('section.alarms').waitFor();
    check('Alarm rules tab and register popup (admin, keyboard and mouse)');

    // 작업지시 작성 팝업도 같은 키보드 동작을 쓴다.
    await admin.locator('nav.shell-menu')
      .getByRole('link', {
        name: '작업지시',
        exact: true
      })
      .click();
    await heading(admin, '작업지시');
    await keyboardPopup(
      admin,
      button(admin.locator('main .toolbar'), '작업지시 작성'),
      '작업지시 작성'
    );
    check('Order create popup works by keyboard');

    // 고정 틀: 낮은 창에서 본문만 맨 아래로 내려도 상단·푸터는 그대로다.
    await admin.setViewportSize({
      width: 1360,
      height: 420
    });
    await fixedFrame(admin, 'Admin orders 1360x420');
    await admin.setViewportSize(desktop);
    check('Header and footer stay fixed while only the body scrolls (desktop)');

    await admin.screenshot({
      path: path.join(output, 'FactoryMES-shell-admin-alarms.png'),
      fullPage: true
    });

    // 7. 작업자(portal): 관리자 메뉴 없음, 관리자 화면 직접 URL은 안내만
    const portal = await login(browser, 'portal', 'portal_password', desktop);
    await portal.goto(base + '/dashboard');
    await heading(portal, '대시보드');
    const portalLabels = (await portal.locator('nav.shell-menu a').allTextContents())
      .map(x => x.trim());
    if (JSON.stringify(portalLabels) !== JSON.stringify(portalMenu)) {
      throw new Error('Portal menu differs: ' + JSON.stringify(portalLabels));
    }
    const portalMarker = await mark(portal);
    await portal.locator('nav.shell-menu')
      .getByRole('link', {
        name: '경보',
        exact: true
      })
      .click();
    await heading(portal, '경보');
    await expectShell(portal, '경보', '/alarms');
    if (await portal.getByRole('tab').count()) {
      throw new Error('Worker must not see alarm rule tabs');
    }
    if (await marker(portal) !== portalMarker) {
      throw new Error('Portal menu navigation reloaded the document');
    }
    await portal.goto(base + '/master-data');
    await portal.getByText('기준정보 관리는 관리자만 사용할 수 있습니다.').waitFor();
    if (await portal.locator('nav.shell-menu a.active').count()) {
      throw new Error('Hidden admin menu must not appear active for worker');
    }
    check('Portal menu has no admin items; same-document navigation; admin URL shows notice');

    // 8. 업무폰 폭(390px): 메뉴는 접혀 있고 "메뉴" 버튼으로 연다. 이동하면 다시 접힌다.
    const phone = await login(browser, 'portal', 'portal_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/dashboard');
    await heading(phone, '대시보드');
    await noPageOverflow(phone, 'Phone dashboard');
    const menu = phone.locator('nav.shell-menu');
    const toggle = button(phone.locator('header.shell-top'), '메뉴');
    if (await menu.isVisible()) {
      throw new Error('Phone menu must start collapsed');
    }
    const phoneMarker = await mark(phone);
    await toggle.click();
    await menu.waitFor({ state: 'visible' });
    if (await toggle.getAttribute('aria-expanded') !== 'true') {
      throw new Error('Menu toggle must report expanded');
    }
    // 메뉴 바깥(어두운 배경)을 누르면 닫힌다.
    await phone.mouse.click(380, 600);
    await menu.waitFor({ state: 'hidden' });
    await toggle.click();
    await menu.waitFor({ state: 'visible' });
    await menu.getByRole('link', {
      name: '알림함',
      exact: true
    }).click();
    await heading(phone, '알림함');
    await menu.waitFor({ state: 'hidden' });
    await expectShell(phone, '알림함', '/inbox');
    await noPageOverflow(phone, 'Phone inbox');
    if (await marker(phone) !== phoneMarker) {
      throw new Error('Phone menu navigation reloaded the document');
    }
    await phone.screenshot({
      path: path.join(output, 'FactoryMES-shell-phone-inbox.png'),
      fullPage: true
    });
    // 좁은 화면 고정 틀: 낮은 창에서 본문만 내려도 상단·푸터는 그대로다.
    await phone.setViewportSize({
      width: 390,
      height: 480
    });
    // 사용자 흐름 그대로: "메뉴" 버튼으로 연 뒤 대시보드 링크를 누른다(이동하면 메뉴가 닫힌다).
    await toggle.click();
    await menu.waitFor({ state: 'visible' });
    await menu.getByRole('link', {
      name: '대시보드',
      exact: true
    }).click();
    await heading(phone, '대시보드');
    await menu.waitFor({ state: 'hidden' });
    await fixedFrame(phone, 'Phone dashboard 390x480');
    check('Phone 390px: menu collapsed, opens by button, closes on backdrop and navigation; '
      + 'header and footer fixed while body scrolls');

    // 9. 상단 로그아웃 한 번으로 로그인 화면
    await button(admin.locator('header.shell-top'), '로그아웃').click();
    await admin.locator('#login-form').waitFor({ state: 'visible' });
    const after = await admin.evaluate(
      async () => (await fetch('/api/v1/auth/me', { cache: 'no-store' })).status
    );
    if (after !== 401) {
      throw new Error('Session still active after shell logout: ' + after);
    }
    check('Shell logout signs out and returns to login');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      dataCreated: 0,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'Navigation and popups only; no records saved. Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-shell-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser shell verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const text = await page.locator('main > p[role="status"]')
          .textContent({ timeout: 1000 })
          .catch(() => 'none');
        console.error(`PAGE ${page.url()} STATUS: ${text}`);
      }
    }
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();


