// Outbox 운영 재처리 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-통계-AI-재처리-동시구현分担-v1.md "3단계" + 2026-11-16 19:09 GPT 확정.
//   작업자 Outbox API 403 → 관리자 화면에서 Mock 시나리오 OUTBOX_FAIL_ONCE 알림 생성
//   → 로컬 인계가 3회 실패해 최종 FAILED(다음 시도 없음) → 화면 "재처리" 팝업(가능 표시, 사유 필수)
//   → 재처리 접수 → 4번째 시도에 DELIVERED, EventID·업무 버전 불변, 이어서 EMAIL·CHAT Mock 수신
//   → 같은 요청 재전송은 같은 결과(중복 처리 없음), 인계 끝난 이벤트는 재처리 불가(ALREADY_HANDED_OFF)
//   → OUTBOX_ALWAYS_FAIL: 낡은 상태 해시 409 → 화면 재처리 → 회차 3회 다시 실패해 시도 6회로 FAILED
//   → 390px 넘침 없음, 캡처. 실제 외부 발송은 없고 이력은 남긴다.
// 실행: GPT 배포 뒤 `node verify-outbox-retry.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const notificationType = 'mes.notification.created.v1';

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

// 알림 생성 이벤트(Outbox)를 최근 목록에서 찾는다.
async function outboxOf(page, notificationId) {
  const list = await api(page, 'GET', '/api/v1/outbox-events?limit=100');
  if (list.status !== 200) {
    return null;
  }
  return list.json.items.find(x => x.type === notificationType && x.aggregateId === notificationId)
    ?? null;
}

async function retryState(page, outboxId) {
  const result = await api(page, 'GET', `/api/v1/outbox-events/${outboxId}/retry-state`);
  if (result.status !== 200) {
    throw new Error(`retry-state HTTP ${result.status} ${JSON.stringify(result.json)}`);
  }
  return result.json;
}

// 자동 재시도(3회)가 끝나 다음 시도가 없는 최종 FAILED가 될 때까지 기다린다.
async function waitTerminalFailed(page, notificationId, minAttempts) {
  return waitUntil(
    () => outboxOf(page, notificationId),
    found => found && found.status === 'FAILED' && found.nextAttemptAt === null
      && found.attempts >= minAttempts,
    `Outbox for notification ${notificationId} did not reach terminal FAILED`
  );
}

// 화면에서 시연 알림을 만들고 알림 ID를 돌려준다.
async function createMock(page, scenario) {
  // 만든 알림 ID는 "발송 이력" 탭의 조회 칸에 채워진다.
  await page.getByRole('tab', {
    name: '발송 이력',
    exact: true
  }).click();
  const demo = page.locator('section.mock-demo');
  await demo.getByLabel('Mock 시나리오').selectOption(scenario);
  await button(demo, '시연 알림 만들기').click();
  await status(page, '시연 알림을 만들었습니다.');
  return page.locator('section.deliveries').getByLabel('알림 ID').inputValue();
}

// 발송 화면의 "Outbox 인계" 탭을 연다(이미 열려 있어도 그대로).
async function showOutboxTab(page) {
  await page.getByRole('tab', {
    name: 'Outbox 인계',
    exact: true
  }).click();
  await page.locator('section.outbox').waitFor();
}

// Outbox 표에서 첫 칸이 정확히 "#ID"인 행.
function outboxRow(page, outboxId) {
  return page.locator('section.outbox tbody tr').filter({
    has: page.locator('td:first-child').getByText('#' + outboxId, { exact: true })
  });
}

// 화면에서 해당 Outbox 행의 재처리 팝업을 열고 사유를 넣어 보낸다(본문을 가로채 기록).
async function retryFromUi(page, outboxId, reason, sent) {
  await showOutboxTab(page);
  await button(page.locator('section.outbox'), 'Outbox 새로고침').click();
  await status(page, 'Outbox 기록을 다시 읽었습니다.');
  const row = outboxRow(page, outboxId);
  await button(row, '재처리').click();
  const dialog = page.locator('form.outbox-retry-dialog');
  await dialog.waitFor();
  await status(page, '재처리 상태를 읽었습니다.');
  await dialog.locator('.outbox-facts').getByText('가능', { exact: true }).waitFor();

  const isRetry = url => url.pathname === `/api/v1/outbox-events/${outboxId}/retry`;
  const capture = route => {
    if (route.request().method() === 'POST') {
      sent.push(JSON.parse(route.request().postData()));
    }
    return route.fallback();
  };
  await page.route(isRetry, capture);
  await button(dialog, '재처리 요청').click();
  await status(page, '재처리 사유: 1~1000자');
  await dialog.getByLabel('재처리 사유').fill(reason);
  await button(dialog, '재처리 요청').click();
  await status(page, '재처리를 접수했습니다.');
  await dialog.waitFor({ state: 'detached' });
  await page.unroute(isRetry, capture);
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

    // 0. 권한: 작업자는 Outbox 조회·재처리 API 403
    const denied = await api(worker, 'GET', '/api/v1/outbox-events');
    if (denied.status !== 403) {
      throw new Error('Worker Outbox API returned ' + denied.status);
    }
    check('Worker Outbox API is denied (403)');

    await admin.goto(base + '/deliveries');
    await admin.getByRole('heading', {
      name: '알림 발송 이력',
      exact: true
    }).waitFor();
    await status(admin, '완료했습니다.');

    // 1. OUTBOX_FAIL_ONCE: 3회 인계 실패 → 최종 FAILED, 재처리 가능
    const failOnce = await createMock(admin, 'OUTBOX_FAIL_ONCE');
    const failed = await waitTerminalFailed(admin, failOnce, 3);
    const before = await retryState(admin, failed.outboxId);
    if (!before.canRetry || before.blockReason !== null || failed.attempts !== 3) {
      throw new Error('FAIL_ONCE terminal state unexpected: ' + JSON.stringify(before));
    }
    check('OUTBOX_FAIL_ONCE ends terminal FAILED after 3 attempts and is retryable');

    // 2. 화면 재처리(빈 사유 거절 → 사유 입력) → 4번째 시도에 DELIVERED, 이벤트 불변
    const sent = [];
    await retryFromUi(admin, failed.outboxId, 'Outbox 로컬 인계 재처리 확인', sent);
    if (sent.length !== 1 || sent[0].expectedStateHash !== before.stateHash) {
      throw new Error('Retry body must carry the observed state hash: ' + JSON.stringify(sent));
    }
    const delivered = await waitUntil(
      () => outboxOf(admin, failOnce),
      found => found && found.status === 'DELIVERED',
      'Retried Outbox event was not handed off'
    );
    if (delivered.attempts !== 4 || delivered.eventId !== failed.eventId
      || delivered.outboxId !== failed.outboxId || delivered.aggregateId !== failed.aggregateId) {
      throw new Error('Retried event must keep identity with cumulative attempts: '
        + JSON.stringify(delivered));
    }
    check('UI retry hands off on 4th attempt; event id and aggregate unchanged');

    const receipts = await waitUntil(
      async () => {
        const list = await api(admin, 'GET', '/api/v1/deliveries?notificationId=' + failOnce);
        if (list.status !== 200 || list.json.items.length < 2) {
          return null;
        }
        const ids = list.json.items.map(x => x.deliveryId).join(',');
        const mock = await api(
          admin,
          'GET',
          '/api/v1/delivery-mock-statuses?deliveryIds=' + ids
        );
        return {
          items: list.json.items,
          mock: mock.json ?? []
        };
      },
      found => found && found.mock.length === found.items.length
        && found.mock.every(x => x.outcome === 'ACCEPTED'),
      'Deliveries after Outbox retry were not Mock-accepted'
    );
    const channels = receipts.items.map(x => x.channel).sort().join(',');
    if (channels !== 'CHAT,EMAIL' || receipts.items.some(x => x.status === 'SENT')) {
      throw new Error('Unexpected deliveries after retry: ' + JSON.stringify(receipts.items));
    }
    check('After handoff, EMAIL and CHAT intents are created once and Mock-accepted (not SENT)');

    // 3. 같은 요청 재전송은 같은 결과, 인계 끝난 이벤트는 재처리 불가
    const replay = await api(
      admin,
      'POST',
      `/api/v1/outbox-events/${failed.outboxId}/retry`,
      sent[0]
    );
    if (replay.status !== 202) {
      throw new Error('Same retry request replay returned ' + replay.status);
    }
    const afterReplay = await outboxOf(admin, failOnce);
    if (afterReplay.attempts !== 4 || afterReplay.status !== 'DELIVERED') {
      throw new Error('Replay must not re-run the event: ' + JSON.stringify(afterReplay));
    }
    const handed = await retryState(admin, failed.outboxId);
    if (handed.canRetry || handed.blockReason !== 'ALREADY_HANDED_OFF') {
      throw new Error('Delivered event must not be retryable: ' + JSON.stringify(handed));
    }
    await showOutboxTab(admin);
    await button(admin.locator('section.outbox'), 'Outbox 새로고침').click();
    await status(admin, 'Outbox 기록을 다시 읽었습니다.');
    const deliveredRow = outboxRow(admin, failed.outboxId);
    if (await deliveredRow.count() && await button(deliveredRow, '재처리').count()) {
      throw new Error('Delivered row must not offer retry');
    }
    check('Same request replays without re-running; delivered event is ALREADY_HANDED_OFF');

    // 4. OUTBOX_ALWAYS_FAIL: 낡은 상태 해시 409 → 화면 재처리 → 회차 3회 다시 실패, 시도 누적 6
    const alwaysFail = await createMock(admin, 'OUTBOX_ALWAYS_FAIL');
    const stuck = await waitTerminalFailed(admin, alwaysFail, 3);
    const stale = await api(admin, 'POST', `/api/v1/outbox-events/${stuck.outboxId}/retry`, {
      clientRequestId: crypto.randomUUID(),
      expectedStateHash: '0'.repeat(64),
      reason: 'stale state hash check'
    });
    if (stale.status !== 409) {
      throw new Error('Stale state hash returned ' + stale.status);
    }
    const sentAgain = [];
    await retryFromUi(admin, stuck.outboxId, 'Outbox 상시 실패 재처리 확인', sentAgain);
    const exhausted = await waitTerminalFailed(admin, alwaysFail, 6);
    if (exhausted.attempts !== 6 || exhausted.eventId !== stuck.eventId) {
      throw new Error(
        'ALWAYS_FAIL must stop at 6 cumulative attempts: ' + JSON.stringify(exhausted)
      );
    }
    check('Stale hash is 409; ALWAYS_FAIL retried from UI fails a new round (3 -> 6 attempts)');

    await showOutboxTab(admin);
    await button(admin.locator('section.outbox'), 'Outbox 새로고침').click();
    await status(admin, 'Outbox 기록을 다시 읽었습니다.');
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-outbox-retry-screen.png'),
      fullPage: true
    });

    // 5. 390px: 본문 넘침 없음
    const phone = await login(browser, 'admin', 'admin_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/deliveries');
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone deliveries with Outbox retry');
    check('Phone width 390: no page overflow; capture saved');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      failOnceNotificationId: failOnce,
      alwaysFailNotificationId: alwaysFail,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'Outbox events are never edited; attempts accumulate. Mock to the admin only; '
        + 'history retained. Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-outbox-retry-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser Outbox retry verified');
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
