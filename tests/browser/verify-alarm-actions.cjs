// 경보 조치(등록·시작·완료)와 정상·강제 종결 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-경보조치종결-동시구현분담-v1.md의 "Claude 화면"과 "검증 fixture".
//   고유 설비 + portal 담당 RELEASED 작업 + 수집 주기 60초·만료 2초 연결 + 규칙(API)
//   → Quartz ACTIVE → 작업자 본인 조치 등록·시작·완료(OPEN 직접 완료는 API 409)
//   → ACTIVE 정상 종결 거절 → 수집 재개로 RECOVERED → ACK → 관리자 정상 종결
//   → 다시 끊어 두 번째 ACTIVE → 미완료 조치가 있으면 강제 종결 거절 → 완료 뒤 강제 종결
//   → FORCED, 복구 시각 없음, 규칙 억제 시각 확인
//   → 정리: 연결·규칙 중지, 작업 취소(이력은 보존). 기존 자료는 건드리지 않는다.
// 실행: GPT 배포 뒤 `node verify-alarm-actions.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'ACT-' + crypto.randomUUID().slice(0, 8).toUpperCase();

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

// 조치 영역 상태 문구를 기다린다. 부모가 경보를 다시 읽는 동안에는 바깥 fieldset이 잠긴다.
async function actionStatus(page, text) {
  await page.waitForFunction(
    expected => document.querySelector('.alarm-actions .action-status')
      ?.textContent.includes(expected)
      && !document.querySelector('main > fieldset')?.disabled
      && !document.querySelector('.alarm-actions fieldset')?.disabled,
    text
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

// 로그인한 브라우저의 쿠키와 CSRF로 API를 호출한다(fixture 준비·정리·권한 확인용).
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
  if (result.status !== expected) {
    throw new Error(`${label}: HTTP ${result.status} ${JSON.stringify(result.json)}`);
  }
  return result.json;
}

// 서버 상태가 조건을 만족할 때까지 짧게 반복 조회한다(Quartz 주기 5초, 최대 90초).
async function waitUntil(action, condition, label, timeoutMs = 90000) {
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

async function openAlarm(page, alarmId) {
  await page.goto(base + '/alarms?alarmId=' + alarmId);
  await page.getByRole('heading', { name: '경보 상세 · #' + alarmId }).waitFor();
  await status(page, '완료했습니다.');
  return page.locator('section.alarm-detail');
}

function settings(pollIntervalMs, staleSec) {
  return {
    protocol: 'HTTP',
    approvedEndpoint: 'http://equipment-simulator:8080/equipment/' + key,
    pollIntervalMs,
    timeoutMs: 1000,
    staleSec,
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
  };
}

// 연결 설정의 수집 주기·만료 시간을 바꾼다(끊김 유도 또는 복구).
async function configure(admin, pollIntervalMs, staleSec, reason) {
  const target = await expectStatus(
    admin,
    'GET',
    '/api/v1/integration-targets/' + key,
    undefined,
    200,
    'Target detail'
  );
  await expectStatus(
    admin,
    'PUT',
    `/api/v1/integration-targets/${key}/configuration`,
    {
      expectedVersion: target.version,
      expectedConfigVersion: target.configVersion,
      displayName: target.displayName,
      enabled: true,
      reason,
      settings: settings(pollIntervalMs, staleSec)
    },
    200,
    'Configure collection'
  );
}

async function waitAlarm(admin, equipmentId, alarmStatus, exceptId, label) {
  const result = await waitUntil(
    () => api(admin, 'GET', `/api/v1/alarms?equipmentId=${equipmentId}&status=${alarmStatus}`),
    found => found.status === 200
      && found.json.items.some(x => x.alarmId !== exceptId),
    label
  );
  return result.json.items.find(x => x.alarmId !== exceptId);
}

function actionItem(detail, text) {
  return detail.locator('.actions li').filter({ hasText: text });
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });

  let admin = null;
  let order = null;
  let targetCreated = false;
  let ruleId = null;

  try {
    admin = await login(browser, 'admin', 'admin_password');
    const worker = await login(browser, 'portal', 'portal_password');

    // 0. fixture: 고유 설비, portal 담당 RELEASED 작업, 장주기·짧은 만료 연결, 규칙
    const me = await expectStatus(worker, 'GET', '/api/v1/auth/me', undefined, 200, 'Worker me');
    const adminMe = await expectStatus(
      admin,
      'GET',
      '/api/v1/auth/me',
      undefined,
      200,
      'Admin me'
    );
    const lines = await expectStatus(
      admin,
      'GET',
      '/api/v1/lines?active=true&limit=100',
      undefined,
      200,
      'Lines'
    );
    const products = await expectStatus(
      admin,
      'GET',
      '/api/v1/products?active=true&limit=1',
      undefined,
      200,
      'Products'
    );
    const equipment = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: key,
        name: '조치 검증 설비 ' + key,
        lineId: lines.items.find(x => x.code === 'DEMO-LINE').lineId,
        active: true
      },
      201,
      'Create equipment'
    );
    const now = new Date();
    order = await expectStatus(
      admin,
      'POST',
      '/api/v1/orders',
      {
        productId: products.items[0].productId,
        equipmentId: equipment.equipmentId,
        assigneeId: me.userId,
        operationLabel: '조치 검증 ' + key,
        targetQty: 10,
        countMode: 'MANUAL',
        counterMappingId: null,
        plannedStart: now.toISOString(),
        plannedEnd: new Date(now.getTime() + 3600000).toISOString(),
        checklist: []
      },
      201,
      'Create assigned order'
    );
    order = await expectStatus(
      admin,
      'POST',
      `/api/v1/orders/${order.orderId}/release`,
      { expectedVersion: order.version },
      200,
      'Release order'
    );
    await expectStatus(
      admin,
      'POST',
      '/api/v1/integration-targets',
      {
        targetId: key,
        displayName: '조치 검증 연결 ' + key,
        connectorType: 'equipment-http',
        enabled: true,
        kind: 'EQUIPMENT',
        equipmentId: equipment.equipmentId,
        settings: settings(60000, 2)
      },
      201,
      'Register long-poll connection'
    );
    targetCreated = true;
    const rule = await expectStatus(
      admin,
      'POST',
      '/api/v1/alarm-rules',
      {
        equipmentId: equipment.equipmentId,
        targetId: key,
        mappingId: null,
        kind: 'COMM_LOST',
        severity: 'WARN',
        triggerHigh: null,
        clearBelow: null,
        holdSec: null,
        clearHoldSec: null,
        enabled: true
      },
      201,
      'Create rule'
    );
    ruleId = rule.ruleId;
    check('Fixture: equipment, assigned RELEASED order, long-poll connection, rule');

    const first = await waitAlarm(
      admin,
      equipment.equipmentId,
      'ACTIVE',
      null,
      'Quartz did not open first alarm'
    );
    check('Quartz opens first COMM_LOST alarm');

    // 1. 작업자: 본인 조치 등록 → OPEN 직접 완료는 거절 → 시작 → 결과 입력 완료
    let detail = await openAlarm(worker, first.alarmId);
    if (await detail.locator('.alarm-close').count()) {
      throw new Error('Worker must not see close controls');
    }
    if (await detail.getByLabel('담당 사용자 ID').count()) {
      throw new Error('Worker must not choose another owner');
    }
    await detail.getByLabel('조치 내용').fill('통신 케이블 점검 ' + key);
    await button(detail, '조치 등록').click();
    await actionStatus(worker, '조치를 등록했습니다.');
    const own = actionItem(detail, '통신 케이블 점검 ' + key);
    await own.filter({ hasText: 'OPEN' }).waitFor();
    await own.filter({ hasText: '담당 사용자 #' + me.userId }).waitFor();
    if (await detail.getByLabel('조치 내용').inputValue() !== '') {
      throw new Error('Content input must clear after create');
    }
    check('Worker registers own action (OPEN); worker has no close/owner controls');

    const actions = await expectStatus(
      worker,
      'GET',
      `/api/v1/alarms/${first.alarmId}/actions`,
      undefined,
      200,
      'Action list'
    );
    const ownAction = actions.items.find(x => x.content.includes(key));
    const alarmNow = await expectStatus(
      worker,
      'GET',
      '/api/v1/alarms/' + first.alarmId,
      undefined,
      200,
      'Alarm detail'
    );
    const direct = await api(
      worker,
      'POST',
      `/api/v1/alarm-actions/${ownAction.actionId}/complete`,
      {
        expectedVersion: ownAction.version,
        expectedAlarmVersion: alarmNow.version,
        result: 'skip start'
      }
    );
    if (direct.status !== 409) {
      throw new Error('OPEN action direct complete returned ' + direct.status);
    }
    if (await button(own, '조치 완료').count()) {
      throw new Error('OPEN action must not show complete button');
    }
    check('OPEN action cannot be completed directly (API 409, no UI button)');

    // 조치 목록 GET 실패 주입: 시작 뒤 새 버전 목록을 못 읽으면 변경을 막고, 재조회로 풀린다.
    // 같은 렌더 사이에 목록 조회가 여러 번 나갈 수 있어, 확인할 때까지 이 경보 목록 GET만 실패시킨다.
    await detail.getByLabel('조치 내용').fill('입력 보존 확인');
    let injected = 0;
    const listPath = `/api/v1/alarms/${first.alarmId}/actions`;
    const failList = route => {
      if (route.request().method() === 'GET') {
        injected += 1;
        return route.fulfill({
          status: 500,
          contentType: 'application/problem+json',
          body: JSON.stringify({
            status: 500,
            title: 'Injected failure',
            code: 'InjectedFailure'
          })
        });
      }
      return route.fallback();
    };
    const isList = url => url.pathname === listPath;
    await worker.route(isList, failList);
    await button(own, '조치 시작').click();
    await worker.waitForFunction(
      () => document.querySelector('.alarm-actions .action-status')
        ?.textContent.includes('조치 목록을 읽지 못했습니다')
        && !document.querySelector('main > fieldset')?.disabled
    );
    await worker.unroute(isList, failList);
    if (injected < 1) {
      throw new Error('Action list GET was not attempted after start');
    }
    // fieldset 자체는 Playwright isDisabled 대상이 아니어서 DOM 속성과 안쪽 버튼으로 확인한다.
    const blocked = await detail.locator('.alarm-actions fieldset')
      .evaluate(element => element.disabled);
    if (!blocked || !(await button(detail, '조치 등록').isDisabled())) {
      throw new Error('Action changes must be blocked while list reload failed');
    }
    if (await detail.getByLabel('조치 내용').inputValue() !== '입력 보존 확인') {
      throw new Error('Content input must be kept after list failure');
    }
    await button(detail, '조치 목록 다시 조회').click();
    await actionStatus(worker, '조치 목록을 다시 읽었습니다.');
    await own.filter({ hasText: 'IN_PROGRESS' }).waitFor();
    if (await button(detail, '조치 목록 다시 조회').count()) {
      throw new Error('List retry button must disappear after successful reload');
    }
    if (await detail.getByLabel('조치 내용').inputValue() !== '입력 보존 확인') {
      throw new Error('Content input must survive list reload');
    }
    await detail.getByLabel('조치 내용').fill('');
    check('Failed action list GET blocks changes; manual reload unblocks and keeps input');
    await button(own, '조치 완료').click();
    await actionStatus(worker, '조치 결과: 1~1000자');
    await own.getByLabel('조치 결과').fill('커넥터 재체결 완료');
    await button(own, '조치 완료').click();
    await actionStatus(worker, '조치를 완료했습니다.');
    await own.filter({ hasText: 'DONE' }).filter({ hasText: '결과: 커넥터 재체결 완료' }).waitFor();
    await detail.getByText('상태: ACTIVE').waitFor();
    check('Worker starts and completes action with result; alarm stays ACTIVE');

    // 2. 관리자: ACTIVE 경보는 정상 종결 거절(입력 유지)
    detail = await openAlarm(admin, first.alarmId);
    const close = detail.locator('.alarm-close');
    await close.getByText('완료한 조치: 1건').waitFor();
    await close.getByLabel('종결 사유').fill('점검 완료 후 종결');
    await button(close, '정상 종결').click();
    await actionStatus(admin, '충돌했거나 지금 상태에서 할 수 없습니다');
    if (await close.getByLabel('종결 사유').inputValue() !== '점검 완료 후 종결') {
      throw new Error('Close reason must be kept after refusal');
    }
    await detail.getByText('상태: ACTIVE').waitFor();
    check('Normal close on ACTIVE alarm is refused; reason input kept');

    // 3. 수집 재개 → RECOVERED → 작업자 ACK → 관리자 정상 종결
    await configure(admin, 1000, 20, 'Restore regular collection');
    await waitUntil(
      () => api(admin, 'GET', '/api/v1/alarms/' + first.alarmId),
      result => result.status === 200 && result.json.status === 'RECOVERED',
      'Quartz did not recover first alarm'
    );
    detail = await openAlarm(worker, first.alarmId);
    await button(detail, '경보 확인(ACK)').click();
    await status(worker, '완료했습니다.');
    await detail.getByText('확인(ACK): 사용자 #' + me.userId, { exact: false }).waitFor();

    detail = await openAlarm(admin, first.alarmId);
    await detail.getByText('상태: RECOVERED').waitFor();
    await detail.locator('.alarm-close').getByLabel('종결 사유').fill('복구·확인·조치 완료');
    await button(detail.locator('.alarm-close'), '정상 종결').click();
    await actionStatus(admin, '경보를 정상 종결했습니다.');
    await detail.getByText('상태: CLOSED').waitFor();
    await detail.getByText('종결 사유: 복구·확인·조치 완료').waitFor();
    await detail.getByText('NORMAL', { exact: false }).first().waitFor();
    if (await detail.locator('.alarm-close').count()
      || await detail.locator('form.action-create').count()) {
      throw new Error('Closed alarm must not offer close or action input');
    }
    const closed = await expectStatus(
      admin,
      'GET',
      '/api/v1/alarms/' + first.alarmId,
      undefined,
      200,
      'Closed alarm'
    );
    if (closed.closeKind !== 'NORMAL' || !closed.recoveredAt) {
      throw new Error('Normal close result unexpected: ' + JSON.stringify(closed));
    }
    check('RECOVERED + ACK + done action: admin closes NORMAL; closed alarm is read-only');

    // 4. 다시 끊어 두 번째 경보 → 미완료 조치가 있으면 강제 종결 거절 → 완료 뒤 강제 종결
    await configure(admin, 60000, 2, 'Stop collection again for force close');
    const second = await waitAlarm(
      admin,
      equipment.equipmentId,
      'ACTIVE',
      first.alarmId,
      'Quartz did not open second alarm'
    );
    check('Quartz opens second alarm on same rule');

    detail = await openAlarm(admin, second.alarmId);
    await detail.getByLabel('담당 사용자 ID').fill(adminMe.userId);
    await detail.getByLabel('조치 내용').fill('관리자 원격 점검 ' + key);
    await button(detail, '조치 등록').click();
    await actionStatus(admin, '조치를 등록했습니다.');
    const adminAction = actionItem(detail, '관리자 원격 점검 ' + key);
    await adminAction.filter({ hasText: '담당 사용자 #' + adminMe.userId }).waitFor();

    // 작업자는 다른 사람 조치에 버튼이 없고, API로도 시작할 수 없다.
    const workerDetail = await openAlarm(worker, second.alarmId);
    const foreign = actionItem(workerDetail, '관리자 원격 점검 ' + key);
    await foreign.waitFor();
    if (await button(foreign, '조치 시작').count()) {
      throw new Error('Worker must not see start on another owner action');
    }
    const secondActions = await expectStatus(
      worker,
      'GET',
      `/api/v1/alarms/${second.alarmId}/actions`,
      undefined,
      200,
      'Second action list'
    );
    const foreignAction = secondActions.items[0];
    const secondNow = await expectStatus(
      worker,
      'GET',
      '/api/v1/alarms/' + second.alarmId,
      undefined,
      200,
      'Second alarm detail'
    );
    const foreignStart = await api(
      worker,
      'POST',
      `/api/v1/alarm-actions/${foreignAction.actionId}/start`,
      {
        expectedVersion: foreignAction.version,
        expectedAlarmVersion: secondNow.version
      }
    );
    if (foreignStart.status !== 403) {
      throw new Error('Worker start on another owner action returned ' + foreignStart.status);
    }
    check('Admin assigns action to an owner; worker cannot change it (no button, API 403)');

    const force = detail.locator('.alarm-close');
    await force.getByLabel('종결 사유').fill('설비 철거 예정으로 강제 종결');
    if (!(await button(force, '강제 종결').isDisabled())) {
      throw new Error('Force close must need explicit confirmation');
    }
    await force.getByLabel('복구가 아니며 30분 동안 새 경보가 억제됨을 확인했습니다.').check();
    await button(force, '강제 종결').click();
    await actionStatus(admin, '충돌했거나 지금 상태에서 할 수 없습니다');
    await detail.getByText('상태: ACTIVE').waitFor();
    check('Force close needs confirmation and is refused while an action is open');

    await button(adminAction, '조치 시작').click();
    await actionStatus(admin, '조치를 시작했습니다.');
    await adminAction.getByLabel('조치 결과').fill('원격 점검 완료');
    await button(adminAction, '조치 완료').click();
    await actionStatus(admin, '조치를 완료했습니다.');
    await adminAction.filter({ hasText: 'DONE' }).waitFor();

    if (await force.getByLabel('종결 사유').inputValue() !== '설비 철거 예정으로 강제 종결') {
      throw new Error('Force close reason must be kept across refusal and action changes');
    }
    const confirm = force.getByLabel('복구가 아니며 30분 동안 새 경보가 억제됨을 확인했습니다.');
    if (!(await confirm.isChecked())) {
      await confirm.check();
    }
    await button(force, '강제 종결').click();
    await actionStatus(admin, '경보를 강제 종결했습니다.');
    await detail.getByText('상태: CLOSED').waitFor();
    await detail.getByText('복구: -').waitFor();
    const forced = await expectStatus(
      admin,
      'GET',
      '/api/v1/alarms/' + second.alarmId,
      undefined,
      200,
      'Forced alarm'
    );
    if (forced.closeKind !== 'FORCED' || forced.recoveredAt !== null) {
      throw new Error('Force close result unexpected: ' + JSON.stringify(forced));
    }
    const suppressed = await expectStatus(
      admin,
      'GET',
      '/api/v1/alarm-rules/' + ruleId,
      undefined,
      200,
      'Suppressed rule'
    );
    const minutes = (new Date(suppressed.suppressUntil) - Date.now()) / 60000;
    if (!(minutes > 25 && minutes <= 31)) {
      throw new Error('Rule suppression not about 30 minutes: ' + suppressed.suppressUntil);
    }
    // 경보 상세는 팝업이라 닫은 뒤 바깥 버튼을 누른다. 규칙 표는 전체 새로고침 때 다시 읽는다.
    await button(detail, '닫기').click();
    await detail.waitFor({ state: 'detached' });
    await button(admin, '경보 새로고침').click();
    await status(admin, '완료했습니다.');
    // 규칙 표는 관리자 전용 "경보 규칙" 탭에 있다.
    await admin.getByRole('tab', {
      name: '경보 규칙',
      exact: true
    }).click();
    await admin.locator('section.rules')
      .getByRole('row')
      .filter({ hasText: key })
      .filter({ hasText: '까지' })
      .waitFor();
    check('Admin force-closes after action done: FORCED, no recovery time, rule suppressed');

    // 억제 중에는 연결·규칙을 켠 채로 데이터가 계속 끊겨 있어도 Quartz가 새 경보를 열지 않는다.
    // 30분 만료까지 기다리지 않고, 감시 주기(5초)를 두 번 넘게 기다려 확인한다.
    const target = await expectStatus(
      admin,
      'GET',
      '/api/v1/integration-targets/' + key,
      undefined,
      200,
      'Target during suppression'
    );
    if (!target.enabled || !suppressed.enabled) {
      throw new Error('Connection and rule must stay enabled for suppression check');
    }
    await new Promise(resolve => setTimeout(resolve, 12000));
    const reopened = await expectStatus(
      admin,
      'GET',
      `/api/v1/alarms?equipmentId=${equipment.equipmentId}&status=ACTIVE`,
      undefined,
      200,
      'Active alarms during suppression'
    );
    if (reopened.items.length !== 0) {
      throw new Error('Suppressed rule opened a new alarm: ' + JSON.stringify(reopened.items));
    }
    check('Suppressed rule opens no new alarm on later watch cycles (enabled, still stale)');

    await admin.screenshot({
      path: path.join(output, 'FactoryMES-alarm-actions-screen.png'),
      fullPage: true
    });

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      testKey: key,
      normalClosedAlarmId: first.alarmId,
      forceClosedAlarmId: second.alarmId,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'Fixture connection/rule disabled and order cancelled; history retained. '
        + 'Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-alarm-actions-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser alarm actions verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const text = await page.locator('main > p[role="status"]')
          .textContent()
          .catch(() => 'none');
        const action = await page.locator('.alarm-actions .action-status')
          .textContent({ timeout: 1000 })
          .catch(() => 'none');
        console.error('PAGE STATUS: ' + text + ' / ACTION STATUS: ' + action);
      }
    }
    process.exitCode = 1;
  } finally {
    // 정리: 자체 fixture만 중지·취소한다(삭제하지 않고 이력은 남긴다).
    if (admin) {
      try {
        if (targetCreated) {
          const current = await api(admin, 'GET', '/api/v1/integration-targets/' + key);
          if (current.status === 200 && current.json.enabled) {
            await api(admin, 'PUT', `/api/v1/integration-targets/${key}/state`, {
              expectedVersion: current.json.version,
              expectedConfigVersion: current.json.configVersion,
              enabled: false,
              reason: 'Browser alarm action verification complete'
            });
          }
        }
        if (ruleId) {
          const rule = await api(admin, 'GET', '/api/v1/alarm-rules/' + ruleId);
          if (rule.status === 200 && rule.json.enabled) {
            // 서버는 계약에 없는 필드를 거절하므로 RuleUpdate 필드만 보낸다.
            await api(admin, 'PUT', '/api/v1/alarm-rules/' + ruleId, {
              expectedVersion: rule.json.version,
              equipmentId: rule.json.equipmentId,
              targetId: rule.json.targetId,
              mappingId: rule.json.mappingId,
              kind: rule.json.kind,
              severity: rule.json.severity,
              triggerHigh: rule.json.triggerHigh,
              clearBelow: rule.json.clearBelow,
              holdSec: rule.json.holdSec,
              clearHoldSec: rule.json.clearHoldSec,
              enabled: false
            });
          }
        }
        if (order) {
          const current = await api(admin, 'GET', '/api/v1/orders/' + order.orderId);
          if (current.status === 200 && current.json.status === 'RELEASED') {
            await api(admin, 'POST', `/api/v1/orders/${order.orderId}/cancel`, {
              expectedVersion: current.json.version,
              reason: 'Browser alarm action verification complete'
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
