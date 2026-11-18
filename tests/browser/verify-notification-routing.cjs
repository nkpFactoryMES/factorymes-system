// 알림 채널 설정이 실제 업무 알림(경보·수동 AI 분석)의 발송 원장을 정하는지 API로 끝까지 확인한다.
// 규격: AI협업폴더 FactoryMES-수신자채널-업무라우팅-동시구현분담-v1.md.
//   fixture: 고유 설비 + 1초 HTTP 온도 수집(시뮬레이터 25.0~27.9℃ 반복) + HIGH 규칙(26↑ 발생, 25.8↓ 복구)
//     → 경보가 발생·복구를 반복한다. 관리자는 경보 수신자다(이 설비에 작업 담당자는 없음).
//   1. 관리자 설정: ALARM_OPENED 이메일 LOCAL·채팅 끔, ALARM_RECOVERED 이메일 끔·채팅 MOCK
//      → 발생 알림: 관리자 EMAIL 원장 1개(local://…/SUCCESS)만, 로컬 수신 ACCEPTED(Mailpit)
//      → 복구 알림: 관리자 CHAT 원장 1개(mock://…/SUCCESS)만, Mock 수신 ACCEPTED
//      → 설정이 없는 다른 수신자는 원장 없음(알림함만)
//   2. 설정 변경은 새 알림에만: 발생 알림이 생긴 뒤 ALARM_OPENED를 끔으로 바꾸면 기존 원장은 그대로,
//      다음 발생 알림에는 관리자 원장이 없다.
//   3. 수동 AI(Mock 대상) 완료: AI_COMPLETED 이메일 MOCK·채팅 LOCAL → 요청자에게 알림 1건
//      (analysisResultId 연결), EMAIL Mock ACCEPTED·CHAT 로컬 ACCEPTED(채팅 수신기)
//   정리: 관리자 설정을 시작 값으로 되돌리고 규칙·수집을 끈다(이력은 남긴다).
// 예약 AI(AI_SCHEDULED) 실행 경로는 매일 예약·GPU가 필요해 여기서 다루지 않는다.
// 실제 외부 발송·유료 호출 없음. 실행: Backend·Web 배포와 DDL 적용 뒤
// `node verify-notification-routing.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'RT-' + crypto.randomUUID().slice(0, 8).toUpperCase();
const aiKey = key + 'AI';

const checks = [];
const contexts = [];

function check(name) {
  checks.push(name);
  console.log('PASS ' + name);
}

async function login(browser, user, secret) {
  const context = await browser.newContext({ ignoreHTTPSErrors: true });
  contexts.push(context);
  await context.route('**/*', route => {
    const hostname = new URL(route.request().url()).hostname;
    return hostname === 'localhost' ? route.continue() : route.abort();
  });
  const page = await context.newPage();
  await page.goto(base);
  await page.locator('#login-form').waitFor({ state: 'visible' });
  await page.locator('#login').fill(user);
  const secrets = process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets');
  await page.locator('#password').fill(
    fs.readFileSync(path.join(secrets, secret), 'utf8').trim()
  );
  await page.getByRole('button', {
    name: '로그인',
    exact: true
  }).click();
  await page.locator('#workspace').waitFor({ state: 'visible' });
  return page;
}

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

async function expectStatus(page, method, url, body, expected, label) {
  const result = await api(page, method, url, body);
  const allowed = Array.isArray(expected) ? expected : [expected];
  if (!allowed.includes(result.status)) {
    throw new Error(`${label}: ${method} ${url} HTTP ${result.status} `
      + JSON.stringify(result.json));
  }
  return result.json;
}

async function waitUntil(action, condition, label, timeoutMs = 120000) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    last = await action();
    if (condition(last)) {
      return last;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(label + ' (last: ' + JSON.stringify(last)?.slice(0, 500) + ')');
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function setMode(page, userId, kind, emailMode, chatMode, reason) {
  const current = (await expectStatus(
    page,
    'GET',
    '/api/v1/notification-settings/users/' + userId,
    undefined,
    200,
    'Settings'
  )).settings.find(x => x.eventKind === kind);
  return expectStatus(
    page,
    'PUT',
    `/api/v1/notification-settings/users/${userId}/${kind}`,
    {
      expectedVersion: current.version,
      emailMode,
      chatMode,
      reason
    },
    200,
    'Set ' + kind
  );
}

// 관리자 알림함에서 조건에 맞는 알림을 찾는다(최신 순).
async function notices(page, predicate) {
  const inbox = await expectStatus(page, 'GET', '/api/v1/inbox?limit=100', undefined, 200, 'Inbox');
  return inbox.items.filter(predicate);
}

// 한 알림의 발송 원장과 로컬·Mock 수신 상태.
async function deliveriesOf(page, notificationId) {
  const list = await expectStatus(
    page,
    'GET',
    '/api/v1/deliveries?notificationId=' + notificationId,
    undefined,
    200,
    'Deliveries'
  );
  if (list.items.length === 0) {
    return {
      items: [],
      local: [],
      mock: []
    };
  }
  const ids = list.items.map(x => x.deliveryId).join(',');
  return {
    items: list.items,
    local: (await api(page, 'GET', '/api/v1/delivery-local-statuses?deliveryIds=' + ids)).json,
    mock: (await api(page, 'GET', '/api/v1/delivery-mock-statuses?deliveryIds=' + ids)).json
  };
}

function floorMinute(date) {
  return new Date(Math.floor(date.getTime() / 60000) * 60000);
}

async function serverNow(page) {
  const header = await page.evaluate(async () => {
    const response = await fetch('/api/v1/auth/csrf', { cache: 'no-store' });
    return response.headers.get('X-Mes-Time');
  });
  return new Date(Date.parse(header));
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });

  let admin = null;
  let adminId = null;
  let original = null;
  let ruleId = null;
  let targetCreated = false;
  const evidence = {};

  try {
    admin = await login(browser, 'admin', 'admin_password');
    adminId = (await expectStatus(admin, 'GET', '/api/v1/auth/me', undefined, 200, 'Me')).userId;
    original = (await expectStatus(
      admin,
      'GET',
      '/api/v1/notification-settings/users/' + adminId,
      undefined,
      200,
      'Admin settings'
    )).settings;

    // 0. fixture: 설비·1초 온도 수집·HIGH 규칙
    const lines = await expectStatus(
      admin,
      'GET',
      '/api/v1/lines?active=true&limit=100',
      undefined,
      200,
      'Lines'
    );
    const equipment = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: key,
        name: '알림 라우팅 검증 설비 ' + key,
        lineId: lines.items.find(x => x.code === 'DEMO-LINE').lineId,
        active: true
      },
      201,
      'Create equipment'
    );
    const target = await expectStatus(
      admin,
      'POST',
      '/api/v1/integration-targets',
      {
        targetId: key,
        displayName: '알림 라우팅 검증 연결 ' + key,
        connectorType: 'equipment-http',
        enabled: true,
        kind: 'EQUIPMENT',
        equipmentId: equipment.equipmentId,
        settings: {
          protocol: 'HTTP',
          approvedEndpoint: 'http://equipment-simulator:8080/equipment/' + key,
          pollIntervalMs: 1000,
          timeoutMs: 1000,
          staleSec: 20,
          credentialRef: null,
          mappings: [
            {
              mappingId: null,
              metricCode: 'TEMPERATURE',
              sourcePath: 'temperature',
              dataType: 'DECIMAL',
              valueKind: 'NUMBER',
              unit: 'C',
              scale: '1',
              offset: '0',
              enabled: true
            }
          ]
        }
      },
      201,
      'Register collection'
    );
    targetCreated = true;
    const collectionStart = await serverNow(admin);

    // 1. 관리자 설정(경보 발생: 이메일 LOCAL, 복구: 채팅 MOCK)
    await setMode(admin, adminId, 'ALARM_OPENED', 'LOCAL', 'OFF', '라우팅 검증: 발생 이메일 로컬');
    await setMode(admin, adminId, 'ALARM_RECOVERED', 'OFF', 'MOCK', '라우팅 검증: 복구 채팅 Mock');

    const rule = await expectStatus(
      admin,
      'POST',
      '/api/v1/alarm-rules',
      {
        equipmentId: equipment.equipmentId,
        targetId: target.targetId,
        mappingId: target.config.content.settings.mappings[0].mappingId,
        kind: 'HIGH',
        severity: 'WARN',
        triggerHigh: '26',
        clearBelow: '25.8',
        holdSec: 2,
        clearHoldSec: 2,
        enabled: true
      },
      201,
      'Create HIGH rule'
    );
    ruleId = rule.ruleId;
    check('Fixture: equipment, 1s temperature collection, HIGH rule; admin channel settings set');

    // 2. 발생 알림 → 관리자 EMAIL(LOCAL)만, 로컬 수신 확인
    const opened = await waitUntil(
      () => notices(admin, x => x.alarmId && x.title === '측정값 상한 초과'
        && x.body.includes(key)),
      found => found.length >= 1,
      'No HIGH opened notification'
    );
    const openedNotice = opened[opened.length - 1];
    const openedDeliveries = await waitUntil(
      () => deliveriesOf(admin, openedNotice.notificationId),
      found => found.items.length >= 1 && found.local.some(x => x.outcome === 'ACCEPTED'),
      'Opened notification was not routed to local EMAIL'
    );
    const adminOpened = openedDeliveries.items.filter(x => x.recipientId === adminId);
    if (adminOpened.length !== 1 || adminOpened[0].channel !== 'EMAIL'
      || adminOpened[0].destinationKey !== 'local.email.user.' + adminId
      || openedDeliveries.items.some(x => x.recipientId !== adminId)) {
      throw new Error('Opened routing unexpected: ' + JSON.stringify(openedDeliveries.items));
    }
    evidence.openedNotificationId = openedNotice.notificationId;
    check('Alarm opened: admin gets only EMAIL via LOCAL (Mailpit accepted); others Inbox only');

    // 3. 설정 변경은 새 알림에만 적용된다.
    await setMode(admin, adminId, 'ALARM_OPENED', 'OFF', 'OFF', '라우팅 검증: 발생 알림 채널 끔');

    // 4. 복구 알림 → 관리자 CHAT(MOCK)만
    const recovered = await waitUntil(
      () => notices(admin, x => x.alarmId && x.title === '측정값 정상 복귀'
        && x.body.includes(key)),
      found => found.length >= 1,
      'No HIGH recovered notification',
      180000
    );
    const recoveredNotice = recovered[recovered.length - 1];
    const recoveredDeliveries = await waitUntil(
      () => deliveriesOf(admin, recoveredNotice.notificationId),
      found => found.items.length >= 1 && found.mock.some(x => x.outcome === 'ACCEPTED'),
      'Recovered notification was not routed to Mock CHAT'
    );
    const adminRecovered = recoveredDeliveries.items.filter(x => x.recipientId === adminId);
    if (adminRecovered.length !== 1 || adminRecovered[0].channel !== 'CHAT'
      || adminRecovered[0].destinationKey !== 'mock.chat.user.' + adminId
      || recoveredDeliveries.items.some(x => x.recipientId !== adminId)) {
      throw new Error('Recovered routing unexpected: ' + JSON.stringify(recoveredDeliveries.items));
    }
    evidence.recoveredNotificationId = recoveredNotice.notificationId;
    check('Alarm recovered: admin gets only CHAT via MOCK (Mock accepted)');

    // 5. 다음 발생 알림: 관리자 원장 없음, 이전 발생 알림 원장은 그대로
    const reopened = await waitUntil(
      () => notices(admin, x => x.alarmId && x.title === '측정값 상한 초과'
        && x.body.includes(key) && x.notificationId !== openedNotice.notificationId),
      found => found.length >= 1,
      'No second opened notification',
      180000
    );
    await sleep(6000);
    const reopenedDeliveries = await deliveriesOf(admin, reopened[0].notificationId);
    const firstAgain = await deliveriesOf(admin, openedNotice.notificationId);
    if (reopenedDeliveries.items.some(x => x.recipientId === adminId)
      || JSON.stringify(firstAgain.items.map(x => [x.deliveryId, x.channel]))
        !== JSON.stringify(adminOpened.map(x => [x.deliveryId, x.channel]))) {
      throw new Error('Setting change must only affect new notifications: '
        + JSON.stringify({
          reopened: reopenedDeliveries.items,
          first: firstAgain.items
        }));
    }
    check('Setting change applies to new notifications only; earlier delivery unchanged');

    // 6. 수동 AI(Mock) 완료 → 요청자 AI_COMPLETED: 이메일 MOCK, 채팅 LOCAL
    await setMode(admin, adminId, 'AI_COMPLETED', 'MOCK', 'LOCAL', '라우팅 검증: AI 완료 채널');
    await expectStatus(
      admin,
      'POST',
      '/api/v1/ai-targets',
      {
        targetId: aiKey,
        displayName: '라우팅 검증 Mock ' + aiKey,
        providerKind: 'MOCK'
      },
      201,
      'Create Mock AI target'
    );
    let periodEnd = null;
    await waitUntil(
      async () => {
        periodEnd = floorMinute(await serverNow(admin));
        return periodEnd.getTime() - collectionStart.getTime();
      },
      elapsed => elapsed >= 2 * 60000,
      'Collection did not span two minutes',
      240000
    );
    const job = await expectStatus(
      admin,
      'POST',
      '/api/v1/analyses',
      {
        requestId: crypto.randomUUID(),
        equipmentId: equipment.equipmentId,
        targetId: aiKey,
        periodStart: new Date(periodEnd.getTime() - 2 * 60000).toISOString(),
        periodEnd: periodEnd.toISOString(),
        analysisKind: 'TREND_SUMMARY'
      },
      [200, 202],
      'Request analysis'
    );
    const finished = await waitUntil(
      () => expectStatus(admin, 'GET', '/api/v1/analyses/' + job.jobId, undefined, 200, 'Job'),
      found => found.results.length > 0 || ['FAILED', 'TIMED_OUT'].includes(found.state),
      'Analysis did not finish'
    );
    if (finished.results.length !== 1) {
      throw new Error('Analysis ended without result: ' + JSON.stringify(finished));
    }
    const resultId = finished.results[0].resultId;
    const aiNotices = await waitUntil(
      () => notices(admin, x => x.analysisResultId === resultId),
      found => found.length === 1,
      'AI completion notification missing'
    );
    if (aiNotices[0].eventId !== 'analysis-result:' + resultId) {
      throw new Error('AI notification cause id should be the result id: ' + aiNotices[0].eventId);
    }
    const aiDeliveries = await waitUntil(
      () => deliveriesOf(admin, aiNotices[0].notificationId),
      found => found.items.length === 2
        && found.mock.some(x => x.outcome === 'ACCEPTED')
        && found.local.some(x => x.outcome === 'ACCEPTED'),
      'AI completion was not routed to Mock EMAIL and local CHAT'
    );
    const email = aiDeliveries.items.find(x => x.channel === 'EMAIL');
    const chat = aiDeliveries.items.find(x => x.channel === 'CHAT');
    if (email.destinationKey !== 'mock.email.user.' + adminId
      || chat.destinationKey !== 'local.chat.user.' + adminId
      || aiDeliveries.local.find(x => x.deliveryId === chat.deliveryId)?.receiptId
        !== 'webhook:' + chat.deliveryId) {
      throw new Error('AI routing unexpected: ' + JSON.stringify(aiDeliveries));
    }
    evidence.aiNotificationId = aiNotices[0].notificationId;
    check('Manual AI completion: one notice per result; EMAIL Mock + CHAT local webhook accepted');

    fs.writeFileSync(
      path.join(output, 'FactoryMES-notification-routing-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          testKey: key,
          evidence,
          externalDeliveryCalls: 0,
          notCovered: 'Scheduled AI (AI_SCHEDULED) runtime path needs the daily schedule/GPU.',
          note: 'Admin settings restored; rule and collection disabled; history retained.'
        },
        null,
        2
      )
    );
    console.log('Notification routing verified');
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  } finally {
    if (admin) {
      try {
        // 관리자 설정을 시작 값으로 되돌린다.
        if (original) {
          for (const kind of ['ALARM_OPENED', 'ALARM_RECOVERED', 'AI_COMPLETED']) {
            const start = original.find(x => x.eventKind === kind);
            await setMode(
              admin,
              adminId,
              kind,
              start.emailMode,
              start.chatMode,
              '라우팅 검증 종료: 시작 값으로 되돌림'
            );
          }
        }
        if (ruleId) {
          const rule = (await api(admin, 'GET', '/api/v1/alarm-rules/' + ruleId)).json;
          await api(admin, 'PUT', '/api/v1/alarm-rules/' + ruleId, {
            expectedVersion: rule.version,
            equipmentId: rule.equipmentId,
            targetId: rule.targetId,
            mappingId: rule.mappingId,
            kind: rule.kind,
            severity: rule.severity,
            triggerHigh: rule.triggerHigh,
            clearBelow: rule.clearBelow,
            holdSec: rule.holdSec,
            clearHoldSec: rule.clearHoldSec,
            enabled: false
          });
        }
        if (targetCreated) {
          const current = (await api(admin, 'GET', '/api/v1/integration-targets/' + key)).json;
          if (current.enabled) {
            await api(admin, 'PUT', `/api/v1/integration-targets/${key}/state`, {
              expectedVersion: current.version,
              expectedConfigVersion: current.configVersion,
              enabled: false,
              reason: 'Notification routing verification complete'
            });
          }
        }
      } catch (cleanupError) {
        console.error('CLEANUP: ' + cleanupError.message);
        process.exitCode = 1;
      }
    }
    await browser.close();
  }
})();
