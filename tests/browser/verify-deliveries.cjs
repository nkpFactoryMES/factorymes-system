// 알림 발송 이력(Outbox → 이메일/채팅 Mock 전달) 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-Outbox-Mock전달-동시구현분담-v1.md의 "관리자화면"과 "검증 Browser".
//   관리자 화면에서 Mock 시연 알림 SUCCESS → 전달기(Jobs 2초)가 EMAIL·CHAT 발송 의도를 만들고 Mock 수신
//   → 실제 상태 PENDING·보낸 시각 없음 + Mock 수신 완료를 다른 열로 표시 → 수신 내용 팝업
//   → FAIL_ONCE → FAILED → 화면 재시도(사유) → Mock 수신, 시도 횟수 유지
//   → 낡은 버전 재시도 409, 작업자 화면·API 403, 390px 넘침 없음, 캡처.
// 시연 알림은 본인(관리자)에게만 만들고 이력은 남긴다. 실제 발송·외부 호출은 없다.
// 실행: GPT 배포 뒤 `node verify-deliveries.cjs`.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';

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

// 로그인한 브라우저의 쿠키와 CSRF로 API를 호출한다(상태 확인·권한 확인용).
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

async function waitUntil(action, condition, label, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const result = await action();
    if (condition(result)) {
      return result;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
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

// 한 알림의 발송 행과 Mock 결과를 API로 읽는다.
async function deliveriesOf(page, notificationId) {
  const list = await api(page, 'GET', '/api/v1/deliveries?notificationId=' + notificationId);
  if (list.status !== 200 || list.json.items.length === 0) {
    return {
      items: [],
      mock: []
    };
  }
  const ids = list.json.items.map(x => x.deliveryId).join(',');
  const mock = await api(page, 'GET', '/api/v1/delivery-mock-statuses?deliveryIds=' + ids);
  const latest = await api(page, 'GET', '/api/v1/deliveries?notificationId=' + notificationId);
  return {
    items: latest.status === 200 ? latest.json.items : list.json.items,
    mock: mock.status === 200 ? mock.json : []
  };
}

// 화면에서 시연 알림을 만들고, 화면이 채운 알림 ID 필터 값을 돌려준다.
async function createMock(page, scenario) {
  const demo = page.locator('section.mock-demo');
  await demo.getByLabel('Mock 시나리오').selectOption(scenario);
  await button(demo, '시연 알림 만들기').click();
  await status(page, '시연 알림을 만들었습니다.');
  const notificationId = await page.locator('section.deliveries').getByLabel('알림 ID').inputValue();
  if (!/^\d+$/.test(notificationId)) {
    throw new Error('Notification filter was not set after mock creation');
  }
  return notificationId;
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

  try {
    const admin = await login(browser, 'admin', 'admin_password', desktop);
    const worker = await login(browser, 'portal', 'portal_password', desktop);
    const adminMe = (await api(admin, 'GET', '/api/v1/auth/me')).json;

    // 0. 권한: 작업자는 메뉴·화면·API 모두 없음
    await worker.goto(base + '/deliveries');
    await worker.getByText('알림 발송 이력은 관리자만 사용할 수 있습니다.').waitFor();
    // 업무 메뉴(nav.shell-menu)가 그려진 뒤에 관리자 메뉴가 없는지 센다(빈 메뉴로 통과하지 않게).
    await worker.locator('nav.shell-menu a').first().waitFor({ state: 'attached' });
    if (await worker.locator('nav.shell-menu a').filter({ hasText: '발송 이력' }).count()) {
      throw new Error('Worker must not see delivery menu');
    }
    const denied = await api(worker, 'GET', '/api/v1/deliveries');
    if (denied.status !== 403) {
      throw new Error('Worker delivery API returned ' + denied.status);
    }
    check('Worker has no delivery menu, page, or API (403)');

    // 1. 관리자: 실제 발송 꺼짐 안내
    await admin.goto(base + '/deliveries');
    await admin.getByRole('heading', {
      name: '알림 발송 이력',
      exact: true
    }).waitFor();
    await status(admin, '완료했습니다.');
    await admin.locator('.delivery-notice').filter({ hasText: '실제 발송(SENT)이 아닙니다' }).waitFor();
    check('Admin page states real delivery is off and Mock is not SENT');

    // 2. SUCCESS: 전달기가 EMAIL·CHAT 의도를 만들고 Mock 수신, 실제 상태는 PENDING 그대로
    const success = await createMock(admin, 'SUCCESS');
    const accepted = await waitUntil(
      () => deliveriesOf(admin, success),
      found => found.items.length >= 2
        && found.mock.length === found.items.length
        && found.mock.every(x => x.outcome === 'ACCEPTED'),
      'SUCCESS mock was not accepted by dispatcher'
    );
    const channels = accepted.items.map(x => x.channel).sort().join(',');
    if (channels !== 'CHAT,EMAIL' || accepted.items.some(x => x.recipientId !== adminMe.userId)) {
      throw new Error('Unexpected delivery intents: ' + JSON.stringify(accepted.items));
    }
    if (accepted.items.some(x => x.status !== 'PENDING' || x.sentAt !== null)) {
      throw new Error('Mock acceptance must not mark SENT: ' + JSON.stringify(accepted.items));
    }
    await button(admin.locator('section.deliveries'), '목록 새로고침').click();
    await status(admin, '발송 이력을 다시 읽었습니다.');
    const rows = admin.locator('section.deliveries tbody tr');
    if (await rows.count() !== accepted.items.length) {
      throw new Error('Filtered list does not match notification deliveries');
    }
    for (const text of await rows.locator('td:nth-child(4)').allTextContents()) {
      if (text.trim() !== 'PENDING') {
        throw new Error('Actual status column must stay PENDING, got ' + text.trim());
      }
    }
    await rows.first().locator('td:nth-child(5)').filter({ hasText: 'Mock 수신 완료(실발송 아님)' })
      .waitFor();
    check('SUCCESS: EMAIL+CHAT intents, Mock accepted, actual status PENDING and sentAt null');

    // 첫 열은 "#발송ID · v버전"이어야 한다. Razor가 "v@식"을 글자로 내보내면 원시 문자열이 보인다.
    const firstCells = await rows.locator('td:nth-child(1)').allTextContents();
    for (const [index, item] of accepted.items.entries()) {
      const cell = firstCells.find(text => text.includes('#' + item.deliveryId + ' '));
      if (!cell || cell.trim() !== `#${item.deliveryId} · v${item.version}` || cell.includes('@')) {
        throw new Error(`Delivery id/version cell ${index} shows "${cell?.trim()}"`);
      }
    }
    check('First column shows numeric delivery id and version (no raw Razor text)');

    // 3. 수신 내용 팝업
    await button(rows.first(), '수신 내용').click();
    const receipt = admin.locator('section.receipt-dialog');
    await receipt.waitFor();
    await receipt.getByText('실제로 발송되지 않았습니다.').waitFor();
    if (!(await receipt.locator('.receipt-body').textContent()).trim()) {
      throw new Error('Receipt body is empty');
    }
    await button(receipt, '닫기').click();
    await receipt.waitFor({ state: 'detached' });
    check('Mock receipt opens in popup with subject/body/hash and closes');

    // 4. FAIL_ONCE: FAILED → 화면 재시도(사유) → Mock 수신, 시도 횟수 유지
    const failOnce = await createMock(admin, 'FAIL_ONCE');
    const failed = await waitUntil(
      () => deliveriesOf(admin, failOnce),
      found => found.items.length >= 2 && found.items.every(x => x.status === 'FAILED'),
      'FAIL_ONCE did not reach FAILED'
    );
    const target = failed.items[0];

    // 낡은 버전으로 보낸 재시도는 409(API)
    const stale = await api(admin, 'POST', `/api/v1/deliveries/${target.deliveryId}/retry`, {
      expectedVersion: target.version - 1,
      reason: 'stale version check'
    });
    if (stale.status !== 409) {
      throw new Error('Stale retry returned ' + stale.status);
    }
    check('FAIL_ONCE reaches FAILED; stale-version retry is refused (409)');

    await button(admin.locator('section.deliveries'), '목록 새로고침').click();
    await status(admin, '발송 이력을 다시 읽었습니다.');
    const failedRow = admin.locator('section.deliveries tbody tr')
      .filter({ hasText: '#' + target.deliveryId + ' ·' });
    await failedRow.locator('.badge').filter({ hasText: 'FAILED' }).waitFor();
    await button(failedRow, '재시도').click();
    const retry = admin.locator('form.retry-dialog');
    await retry.waitFor();
    await button(retry, '재시도 보내기').click();
    await status(admin, '재시도 사유: 1~1000자');
    await retry.getByLabel('재시도 사유').fill('Mock 수동 재시도 확인');
    await button(retry, '재시도 보내기').click();
    await status(admin, '재시도를 접수했습니다.');
    await retry.waitFor({ state: 'detached' });

    const retried = await waitUntil(
      () => deliveriesOf(admin, failOnce),
      found => found.mock.some(x => x.deliveryId === target.deliveryId && x.outcome === 'ACCEPTED'),
      'Retried delivery was not accepted by Mock'
    );
    const after = retried.items.find(x => x.deliveryId === target.deliveryId);
    if (after.attempts < target.attempts || after.version <= target.version
      || after.status === 'SENT' || after.sentAt !== null) {
      throw new Error('Retry result unexpected: ' + JSON.stringify(after));
    }
    check('Admin retries FAILED with reason in popup; Mock accepts; attempts kept, not SENT');

    // 5. Outbox 인계 기록 표시와 캡처
    await button(admin.locator('section.deliveries'), '목록 새로고침').click();
    await status(admin, '발송 이력을 다시 읽었습니다.');
    // Outbox 인계 기록은 별도 탭이다.
    await admin.getByRole('tab', {
      name: 'Outbox 인계',
      exact: true
    }).click();
    await admin.locator('section.outbox tbody tr').first().waitFor();
    await noPageOverflow(admin, 'Admin deliveries desktop');
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-deliveries-screen.png'),
      fullPage: true
    });
    check('Outbox handoff list renders; desktop capture saved');

    // 6. 390px: 본문 넘침 없음, 필터 조작
    const phone = await login(browser, 'admin', 'admin_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/deliveries');
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone deliveries');
    await phone.locator('section.deliveries').getByLabel('채널 필터').selectOption('EMAIL');
    await button(phone.locator('section.deliveries'), '목록 새로고침').click();
    await status(phone, '발송 이력을 다시 읽었습니다.');
    for (const text of await phone.locator('section.deliveries tbody td:nth-child(3)')
      .allTextContents()) {
      if (!text.trim().startsWith('EMAIL')) {
        throw new Error('Channel filter returned ' + text.trim());
      }
    }
    await noPageOverflow(phone, 'Phone deliveries filtered');
    check('Phone width 390: no page overflow; channel filter works');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      successNotificationId: success,
      failOnceNotificationId: failOnce,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      mockMarkedSent: false,
      note: 'Mock notifications to the admin only; history retained. Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-deliveries-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser deliveries verified');
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
    await browser.close();
  }
})();

