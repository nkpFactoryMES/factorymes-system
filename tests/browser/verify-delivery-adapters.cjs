// 이메일·채팅 로컬 통신 어댑터(LOCAL_SMTP → Mailpit, LOCAL_WEBHOOK → chat-receiver)를
// 실제 브라우저로 확인한다. 규격: AI협업폴더 FactoryMES-이메일채팅어댑터-분담규격-v1.md.
//   작업자: 로컬 시연 API 403
//   → 관리자 화면: 안내 문구, "로컬 통신 시연 알림" SUCCESS(요청 본문은 요청 ID·시나리오뿐)
//     → EMAIL·CHAT 두 발송 모두 로컬 수신 확인, 실제 상태 PENDING·보낸 시각 없음(SENT로 위장 안 함)
//     → 표에 "로컬 수신 확인(실발송 아님)"과 받은 곳(Mailpit/채팅 수신기), Mock 수신으로 섞이지 않음
//     → 수신 내용 팝업(LOCAL_SMTP·LOCAL_WEBHOOK, 실발송 아님 안내, 본문), 영수증 API와 일치
//   → 같은 요청 ID 재전송은 같은 알림, 다른 시나리오는 409
//   → CHAT_TRANSIENT_ONCE: 채팅 첫 429 뒤 자동 재시도로 수신(시도 2회 이상)
//   → CHAT_PERMANENT_FAIL: 재시도 없이 FAILED(시도 1회), 화면 수동 재시도(사유) → 다시 FAILED·시도 증가
//   → CHAT_ALWAYS_FAIL: 자동 재시도 3회 뒤 FAILED
//   → 390px 넘침 없음, 캡처.
// 실제 외부 발송·유료 호출 없음(브라우저는 localhost만). 시연 알림은 본인(관리자)에게만 만들고 이력은 남긴다.
// 실행: GPT가 mailpit·chat-receiver·Backend·Jobs·Web 배포를 마친 뒤 `node verify-delivery-adapters.cjs`.
// Mock 발송 화면 회귀는 verify-deliveries.cjs가 맡는다.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
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

async function waitUntil(action, condition, label, timeoutMs = 90000) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    last = await action();
    if (condition(last)) {
      return last;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(label + ' (last: ' + JSON.stringify(last)?.slice(0, 400) + ')');
}

async function noPageOverflow(page, label) {
  const extra = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  if (extra > 1) {
    throw new Error(`${label}: page is ${extra}px wider than viewport`);
  }
}

// 한 알림의 발송 행과 로컬·Mock 수신 결과를 API로 읽는다.
// 행을 먼저 읽고 수신 결과를 나중에 읽으면, 그 사이 ACK가 커밋돼 "수신 확인 + 옛 행"이 섞일 수 있다.
// 그래서 수신 결과를 읽은 뒤 행을 한 번 더 읽어 그 값을 돌려준다(영수증과 행은 같은 트랜잭션에 커밋).
async function deliveriesOf(page, notificationId) {
  const path = '/api/v1/deliveries?notificationId=' + notificationId;
  const list = await api(page, 'GET', path);
  if (list.status !== 200 || list.json.items.length === 0) {
    return {
      items: [],
      local: [],
      mock: []
    };
  }
  const ids = list.json.items.map(x => x.deliveryId).join(',');
  const local = await api(page, 'GET', '/api/v1/delivery-local-statuses?deliveryIds=' + ids);
  const mock = await api(page, 'GET', '/api/v1/delivery-mock-statuses?deliveryIds=' + ids);
  const latest = await api(page, 'GET', path);
  return {
    items: latest.status === 200 ? latest.json.items : list.json.items,
    local: local.status === 200 ? local.json : [],
    mock: mock.status === 200 ? mock.json : []
  };
}

// 자동 재시도 뒤 수신이 끝난 행: 시도 2회 이상, 다음 시도 없음, 마지막 오류 없음.
function settledAfterRetry(item) {
  return item !== undefined && item.attempts >= 2 && item.nextAttemptAt === null
    && item.lastError === null;
}

function byChannel(found, channel) {
  const item = found.items.find(x => x.channel === channel);
  return {
    item,
    local: item ? found.local.find(x => x.deliveryId === item.deliveryId) : undefined
  };
}

// 화면에서 로컬 시연 알림을 만들고 요청 본문과 화면이 채운 알림 ID를 돌려준다.
async function createLocal(page, scenario) {
  const demo = page.locator('section.local-demo');
  await demo.getByLabel('로컬 시나리오').selectOption(scenario);
  const sent = page.waitForRequest(request => request.method() === 'POST'
    && new URL(request.url()).pathname === '/api/v1/local-notifications');
  await button(demo, '로컬 시연 알림 만들기').click();
  const body = (await sent).postDataJSON();
  await status(page, '로컬 시연 알림을 만들었습니다.');
  const notificationId = await page.locator('section.deliveries')
    .getByLabel('알림 ID')
    .inputValue();
  if (!/^\d+$/.test(notificationId)) {
    throw new Error('Notification filter was not set after local creation');
  }
  return {
    notificationId,
    body
  };
}

async function refresh(page) {
  await button(page.locator('section.deliveries'), '목록 새로고침').click();
  await status(page, '발송 이력을 다시 읽었습니다.');
}

function row(page, deliveryId) {
  return page.locator('section.deliveries tbody tr').filter({ hasText: '#' + deliveryId + ' ·' });
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
  const notifications = {};

  try {
    const admin = await login(browser, 'admin', 'admin_password', desktop);
    const worker = await login(browser, 'portal', 'portal_password', desktop);

    // 0. 권한: 작업자는 로컬 시연 API를 쓸 수 없다.
    const denied = await api(worker, 'POST', '/api/v1/local-notifications', {
      clientRequestId: crypto.randomUUID(),
      scenario: 'SUCCESS'
    });
    if (denied.status !== 403) {
      throw new Error('Worker local notification API returned ' + denied.status);
    }
    check('Worker cannot create local delivery demos (403)');

    // 1. 관리자 화면 안내
    await admin.goto(base + '/deliveries');
    await admin.getByRole('heading', {
      name: '알림 발송 이력',
      exact: true
    }).waitFor();
    await status(admin, '완료했습니다.');
    await admin.locator('.delivery-notice').filter({ hasText: '로컬 수신 확인' }).waitFor();
    await admin.locator('section.local-demo .hint').filter({ hasText: '외부로 나가지 않습니다' })
      .waitFor();
    if (await admin.locator('section.local-demo input').count() !== 0) {
      throw new Error('Local demo must not ask address, key or URL');
    }
    check('Admin sees local SMTP/Webhook demo with no address/key/URL inputs');

    // 2. SUCCESS: EMAIL은 Mailpit(SMTP), CHAT은 채팅 수신기(HTTP)가 받는다. 실제 상태는 PENDING.
    const success = await createLocal(admin, 'SUCCESS');
    notifications.success = success.notificationId;
    if (Object.keys(success.body).sort().join(',') !== 'clientRequestId,scenario'
      || success.body.scenario !== 'SUCCESS') {
      throw new Error('Local request body must only carry id and scenario: '
        + JSON.stringify(success.body));
    }
    const accepted = await waitUntil(
      () => deliveriesOf(admin, success.notificationId),
      found => found.items.length >= 2
        && ['EMAIL', 'CHAT'].every(channel => byChannel(found, channel).local?.outcome
          === 'ACCEPTED'),
      'SUCCESS local deliveries were not both accepted'
    );
    const email = byChannel(accepted, 'EMAIL');
    const chat = byChannel(accepted, 'CHAT');
    for (const { item } of [email, chat]) {
      if (item.status !== 'PENDING' || item.sentAt !== null) {
        throw new Error('Local acceptance must not mark SENT: ' + JSON.stringify(item));
      }
      if (accepted.mock.some(x => x.deliveryId === item.deliveryId && x.outcome === 'ACCEPTED')) {
        throw new Error('Local delivery must not appear as Mock accepted: ' + item.deliveryId);
      }
    }
    if (!email.local.receiptId?.startsWith('smtp:')
      || chat.local.receiptId !== 'webhook:' + chat.item.deliveryId) {
      throw new Error('Unexpected receipt ids: ' + JSON.stringify([email.local, chat.local]));
    }
    check('SUCCESS: EMAIL via SMTP and CHAT via Webhook accepted locally; rows stay PENDING');

    await refresh(admin);
    const emailRow = row(admin, email.item.deliveryId);
    const chatRow = row(admin, chat.item.deliveryId);
    await emailRow.locator('td:nth-child(5)')
      .filter({ hasText: '로컬 수신 확인(실발송 아님)' })
      .filter({ hasText: 'Mailpit(SMTP)' })
      .waitFor();
    await chatRow.locator('td:nth-child(5)')
      .filter({ hasText: '로컬 수신 확인(실발송 아님)' })
      .filter({ hasText: '채팅 수신기(HTTP)' })
      .waitFor();
    await emailRow.locator('.badge').filter({ hasText: 'PENDING' }).waitFor();
    check('Table shows local receipt (not real send) and receiver per channel');

    // 3. 수신 내용 팝업과 영수증 API
    const receipts = {};
    for (const [target, provider] of [
      [email, 'LOCAL_SMTP'],
      [chat, 'LOCAL_WEBHOOK']
    ]) {
      const receipt = await api(
        admin,
        'GET',
        `/api/v1/deliveries/${target.item.deliveryId}/local-receipt`
      );
      if (receipt.status !== 200 || receipt.json.provider !== provider
        || receipt.json.receiptId !== target.local.receiptId
        || receipt.json.channel !== target.item.channel
        || receipt.json.destinationKey !== target.item.destinationKey
        || !receipt.json.body || !/^[0-9a-f]{64}$/.test(receipt.json.messageHash)) {
        throw new Error('Local receipt unexpected: ' + JSON.stringify(receipt).slice(0, 400));
      }
      receipts[provider] = receipt.json;

      await button(row(admin, target.item.deliveryId), '수신 내용').click();
      const dialog = admin.locator('section.receipt-dialog');
      await dialog.waitFor();
      await dialog.locator('h3').filter({ hasText: '로컬 수신 내용' }).waitFor();
      await dialog.getByText('외부로는 발송되지 않았고', { exact: false }).waitFor();
      await dialog.locator('dd').filter({ hasText: provider }).waitFor();
      if ((await dialog.locator('.receipt-body').textContent()).trim() === '') {
        throw new Error('Local receipt body is empty in popup');
      }
      if (provider === 'LOCAL_WEBHOOK') {
        await admin.screenshot({
          path: path.join(output, 'FactoryMES-delivery-adapters-receipt.png'),
          fullPage: true
        });
      }
      await button(dialog, '닫기').click();
      await dialog.waitFor({ state: 'detached' });
    }
    check('Receipt popup and API show LOCAL_SMTP/LOCAL_WEBHOOK receipts with body and hash');

    // 4. 멱등: 같은 요청 ID는 같은 알림, 같은 ID에 다른 시나리오는 409
    const replay = await api(admin, 'POST', '/api/v1/local-notifications', success.body);
    if (![200, 201].includes(replay.status)
      || replay.json.notificationId !== success.notificationId) {
      throw new Error('Replay must return the same notification: ' + JSON.stringify(replay));
    }
    const changed = await api(admin, 'POST', '/api/v1/local-notifications', {
      ...success.body,
      scenario: 'CHAT_ALWAYS_FAIL'
    });
    if (changed.status !== 409) {
      throw new Error('Same request id with another scenario returned ' + changed.status);
    }
    check('Same request id returns the same notification; changed scenario is refused (409)');

    // 5. CHAT_TRANSIENT_ONCE: 첫 429 → 자동 재시도로 수신
    const transient = await createLocal(admin, 'CHAT_TRANSIENT_ONCE');
    notifications.transient = transient.notificationId;
    const recovered = await waitUntil(
      () => deliveriesOf(admin, transient.notificationId),
      found => byChannel(found, 'CHAT').local?.outcome === 'ACCEPTED'
        && settledAfterRetry(byChannel(found, 'CHAT').item)
        && byChannel(found, 'EMAIL').local?.outcome === 'ACCEPTED',
      'CHAT_TRANSIENT_ONCE did not recover'
    );
    const transientChat = byChannel(recovered, 'CHAT').item;
    if (transientChat.attempts < 2 || transientChat.status !== 'PENDING') {
      throw new Error('Transient chat should need an automatic retry: '
        + JSON.stringify(transientChat));
    }
    check('CHAT_TRANSIENT_ONCE: 429 then automatic retry accepted '
      + `(attempts ${transientChat.attempts})`);

    // 6. CHAT_PERMANENT_FAIL: 재시도 없이 FAILED → 화면 수동 재시도 → 다시 FAILED, 시도 증가
    const permanent = await createLocal(admin, 'CHAT_PERMANENT_FAIL');
    notifications.permanent = permanent.notificationId;
    const rejected = await waitUntil(
      () => deliveriesOf(admin, permanent.notificationId),
      found => byChannel(found, 'CHAT').item?.status === 'FAILED'
        && byChannel(found, 'CHAT').local?.outcome === 'FAILED'
        && byChannel(found, 'EMAIL').local?.outcome === 'ACCEPTED',
      'CHAT_PERMANENT_FAIL did not reach FAILED'
    );
    const rejectedChat = byChannel(rejected, 'CHAT');
    if (rejectedChat.item.attempts !== 1 || rejectedChat.local?.outcome !== 'FAILED') {
      throw new Error('Permanent chat failure must not auto-retry: '
        + JSON.stringify(rejectedChat));
    }
    await refresh(admin);
    const failedRow = row(admin, rejectedChat.item.deliveryId);
    await failedRow.locator('td:nth-child(5)').filter({ hasText: '로컬 수신 실패' }).waitFor();
    await button(failedRow, '재시도').click();
    const retry = admin.locator('form.retry-dialog');
    await retry.waitFor();
    await retry.getByLabel('재시도 사유').fill('로컬 채팅 수신기 영구 거절 재시도 확인');
    await button(retry, '재시도 보내기').click();
    await status(admin, '재시도를 접수했습니다.');
    await retry.waitFor({ state: 'detached' });
    const retried = await waitUntil(
      () => deliveriesOf(admin, permanent.notificationId),
      found => {
        const item = byChannel(found, 'CHAT').item;
        return item?.status === 'FAILED' && item.attempts > rejectedChat.item.attempts
          && item.version > rejectedChat.item.version;
      },
      'Manual retry of permanent failure did not run'
    );
    check('CHAT_PERMANENT_FAIL: FAILED after 1 attempt; manual retry runs again and stays FAILED '
      + `(attempts ${byChannel(retried, 'CHAT').item.attempts})`);

    // 7. CHAT_ALWAYS_FAIL: 자동 재시도 3회 뒤 FAILED
    const always = await createLocal(admin, 'CHAT_ALWAYS_FAIL');
    notifications.always = always.notificationId;
    const exhausted = await waitUntil(
      () => deliveriesOf(admin, always.notificationId),
      found => byChannel(found, 'CHAT').item?.status === 'FAILED'
        && byChannel(found, 'CHAT').local?.outcome === 'FAILED',
      'CHAT_ALWAYS_FAIL did not reach FAILED',
      120000
    );
    const exhaustedChat = byChannel(exhausted, 'CHAT');
    if (exhaustedChat.item.attempts !== 3 || exhaustedChat.local?.outcome !== 'FAILED') {
      throw new Error('Always-failing chat must stop after 3 attempts: '
        + JSON.stringify(exhaustedChat));
    }
    check('CHAT_ALWAYS_FAIL: automatic retries stop at 3 attempts with FAILED');

    // 8. 390px: 본문 넘침 없음
    await refresh(admin);
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-delivery-adapters-screen.png'),
      fullPage: true
    });
    const phone = await login(browser, 'admin', 'admin_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/deliveries');
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone deliveries');
    check('Phone width 390: no page overflow');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-delivery-adapters-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          runtimeErrors: errors,
          notifications,
          providers: Object.keys(receipts),
          externalDeliveryCalls: 0,
          cloudApiCalls: 0,
          notCovered: 'Mailpit API message lookup and chat-receiver capture files are verified '
            + 'by GPT LocalDeliveryVerification; this script checks the screen and API.',
          note: 'Demo notifications for the admin only; history retained. '
            + 'Bodies, secrets and cookies not saved.'
        },
        null,
        2
      )
    );
    console.log('Browser delivery adapters verified');
  } catch (error) {
    console.error(error.message);
    console.error('NOTIFICATIONS ' + JSON.stringify(notifications));
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


