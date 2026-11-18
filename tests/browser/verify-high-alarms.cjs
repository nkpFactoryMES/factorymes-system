// HIGH(상한) 경보 규칙 설정과 제조 콘솔 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-HIGH-UI개선-동시구현분담-v1.md의 "실제Mock 시연/검증".
//   고유 설비 + portal 담당 RELEASED 작업 + 수집 주기 1초·만료 20초 연결(TEMPERATURE 1:1)
//   → 관리자 화면에서 HIGH 규칙 등록(발생 26.0 / 복구 25.8 / 유지 2초·2초)
//     복구 25.8이면 30샘플 주기 중 25.0~25.8 9샘플이 복구 창이라 판정 시점과 관계없이 결정적이다.
//   → 시뮬레이터 실제 온도(25.1부터 0.1씩 상승, 30번째 측정에서 25.0)로 ACTIVE
//     (판정은 수집 저장 직후 평가와 Quartz 5초 재평가가 같은 이력 규칙을 쓴다. 테스트는 결과만 본다.)
//   → ACTIVE 중 임계값 변경은 409, 원래 규칙 유지 → 실제 측정으로 RECOVERED
//   → 작업자 알림함·경보 상세·ACK·조치 화면 연계 → 데스크톱 캡처, 390px 폭 가로 넘침 확인
//   → 정리: 연결·규칙 중지, 작업 취소(이력은 보존). 가짜 측정·강제 값 주입은 하지 않는다.
// 실행: GPT 배포 뒤 `node verify-high-alarms.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'HIG-' + crypto.randomUUID().slice(0, 8).toUpperCase();

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

// 로그인한 브라우저의 쿠키와 CSRF로 API를 호출한다(fixture 준비·정리·확인용).
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

// 서버 상태가 조건을 만족할 때까지 짧게 반복 조회한다(수집 직후 평가 또는 Quartz 5초 재평가).
async function waitUntil(action, condition, label, timeoutMs) {
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

async function open(page, route, heading) {
  await page.goto(base + route);
  await page.getByRole('heading', {
    name: heading,
    exact: true
  }).waitFor();
  await status(page, '완료했습니다.');
}

// 본문이 화면 폭보다 넓어지지 않았는지 본다(넓은 표는 section 안에서만 스크롤).
async function noPageOverflow(page, label) {
  const extra = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  if (extra > 1) {
    throw new Error(`${label}: page is ${extra}px wider than viewport`);
  }
}

function settings() {
  return {
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
  };
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

  let admin = null;
  let order = null;
  let targetCreated = false;
  let ruleId = null;

  try {
    admin = await login(browser, 'admin', 'admin_password', desktop);
    const worker = await login(browser, 'portal', 'portal_password', desktop);

    // 0. fixture: 고유 설비, portal 담당 RELEASED 작업, 1초 수집 연결(TEMPERATURE 1:1)
    const me = await expectStatus(worker, 'GET', '/api/v1/auth/me', undefined, 200, 'Worker me');
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
        name: 'HIGH 검증 설비 ' + key,
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
        operationLabel: 'HIGH 검증 ' + key,
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
        displayName: 'HIGH 검증 연결 ' + key,
        connectorType: 'equipment-http',
        enabled: true,
        kind: 'EQUIPMENT',
        equipmentId: equipment.equipmentId,
        settings: settings()
      },
      201,
      'Register 1s collection'
    );
    targetCreated = true;
    const target = await expectStatus(
      admin,
      'GET',
      '/api/v1/integration-targets/' + key,
      undefined,
      200,
      'Target detail'
    );
    const mappingId = target.config.content.settings.mappings[0].mappingId;
    check('Fixture: equipment, assigned RELEASED order, 1s TEMPERATURE collection');

    // 1. 관리자 화면: 연결 → HIGH → 매핑 → 임계·유지 순서로 등록. 잘못된 임계는 화면에서 먼저 막는다.
    await open(admin, '/alarms', '경보');
    await noPageOverflow(admin, 'Admin alarms desktop');
    // 규칙은 "경보 규칙" 탭에서 상단 "규칙 등록" 팝업으로 등록한다.
    await admin.getByRole('tab', {
      name: '경보 규칙',
      exact: true
    }).click();
    await button(admin.locator('main .toolbar'), '규칙 등록').click();
    await admin.getByRole('dialog', { name: '규칙 등록' }).waitFor();
    const rules = admin.locator('section.rules');
    const option = await rules.getByLabel('감시할 연결 대상')
      .locator('option')
      .filter({ hasText: key })
      .getAttribute('value');
    await rules.getByLabel('감시할 연결 대상').selectOption(option);
    await rules.getByLabel('경보 종류').selectOption('HIGH');
    await rules.getByLabel('수치 매핑').selectOption(mappingId);
    await rules.getByLabel('발생 임계값(이상)').fill('26.0');
    await rules.getByLabel('발생 유지 시간(초)').fill('2');
    await rules.getByLabel('복구 임계값(이하)').fill('26.1');
    await rules.getByLabel('복구 유지 시간(초)').fill('2');
    await rules.getByLabel('경보 등급').selectOption('WARN');
    await button(rules, '규칙 저장').click();
    await status(admin, '복구 임계값은 발생 임계값보다 작아야 합니다.');
    check('Form blocks clear threshold not below trigger before sending');

    await rules.getByLabel('복구 임계값(이하)').fill('25.8');
    await button(rules, '규칙 저장').click();
    await status(admin, '완료했습니다.');
    const ruleRow = rules.getByRole('row')
      .filter({ hasText: key })
      .filter({ hasText: '상한 초과' });
    // 서버가 돌려주는 소수 표기(26, 26.0, 26.000000)는 정하지 않았으므로 숫자는 API로 확인한다.
    await ruleRow.filter({ hasText: '≥ 26' }).waitFor();
    const created = await expectStatus(
      admin,
      'GET',
      `/api/v1/alarm-rules?equipmentId=${equipment.equipmentId}`,
      undefined,
      200,
      'Rule list'
    );
    const rule = created.items.find(x => x.kind === 'HIGH');
    ruleId = rule.ruleId;
    if (rule.mappingId !== mappingId || Number(rule.triggerHigh) !== 26
      || Number(rule.clearBelow) !== 25.8 || rule.holdSec !== 2 || rule.clearHoldSec !== 2) {
      throw new Error('Saved HIGH rule differs: ' + JSON.stringify(rule));
    }
    check('Admin registers HIGH rule (mapping, 26.0/25.8, hold 2s/2s) from console');

    // 2. 실제 시뮬레이터 온도가 발생 임계 이상으로 2초 이어지면 서버 HIGH 평가가 연다
    const alarm = await waitUntil(
      () => api(admin, 'GET', `/api/v1/alarms?equipmentId=${equipment.equipmentId}&status=ACTIVE`),
      found => found.status === 200 && found.json.items.some(x => x.kind === 'HIGH'),
      'HIGH alarm was not opened by server evaluation',
      90000
    ).then(found => found.json.items.find(x => x.kind === 'HIGH'));
    check('Real simulator temperature opens HIGH alarm (ACTIVE)');

    // 3. ACTIVE 중 임계값 변경은 409, 원래 규칙은 그대로
    await button(ruleRow, '규칙 수정').click();
    await status(admin, '완료했습니다.');
    await rules.getByLabel('발생 임계값(이상)').fill('26.2');
    await button(rules, '규칙 저장').click();
    await status(admin, '충돌');
    const kept = await expectStatus(
      admin,
      'GET',
      '/api/v1/alarm-rules/' + ruleId,
      undefined,
      200,
      'Rule after refused change'
    );
    if (Number(kept.triggerHigh) !== 26 || kept.version !== rule.version) {
      throw new Error('Rule changed while ACTIVE: ' + JSON.stringify(kept));
    }
    if (await rules.getByLabel('발생 임계값(이상)').inputValue() !== '26.2') {
      throw new Error('Threshold input must be kept after 409');
    }
    await button(rules, '규칙 입력 초기화').click();
    check('Threshold change while ACTIVE is refused (409); rule unchanged; input kept');

    await open(admin, '/alarms', '경보');
    await admin.locator('section.alarms tbody tr')
      .filter({ hasText: '#' + alarm.alarmId })
      .locator('.badge.active')
      .waitFor();
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-high-alarms-screen.png'),
      fullPage: true
    });
    check('Alarm list shows ACTIVE HIGH alarm with status badge');

    // 4. 실제 온도가 복구 임계 이하로 2초 이어지면 RECOVERED(약 35초 주기 + 평가 지연)
    await waitUntil(
      () => api(admin, 'GET', '/api/v1/alarms/' + alarm.alarmId),
      found => found.status === 200 && found.json.status === 'RECOVERED',
      'HIGH alarm was not recovered by server evaluation',
      120000
    );
    check('Real simulator temperature recovers HIGH alarm (RECOVERED)');

    // 5. 작업자: 알림함 → 관련 경보 → ACK, 조치 화면 연계
    await open(worker, '/inbox', '알림함');
    const notice = worker.locator('.notifications li').filter({
      has: worker.locator(`a[href="/alarms?alarmId=${alarm.alarmId}"]`)
    });
    await notice.first().waitFor();
    await noPageOverflow(worker, 'Worker inbox desktop');
    await worker.screenshot({
      path: path.join(output, 'FactoryMES-high-inbox-screen.png'),
      fullPage: true
    });
    await notice.first().getByRole('link', { name: '관련 경보 보기' }).click();
    await worker.getByRole('heading', { name: '경보 상세 · #' + alarm.alarmId }).waitFor();
    await status(worker, '완료했습니다.');
    const detail = worker.locator('section.alarm-detail');
    await detail.getByText('상태: RECOVERED').waitFor();
    await button(detail, '경보 확인(ACK)').click();
    await status(worker, '완료했습니다.');
    await detail.getByText('확인(ACK): 사용자 #' + me.userId, { exact: false }).waitFor();
    await button(detail, '조치 등록').waitFor();
    check('Worker gets notification, opens alarm, acknowledges; action entry available');

    await open(admin, '/orders', '작업지시');
    await noPageOverflow(admin, 'Admin orders desktop');
    await admin.screenshot({
      path: path.join(output, 'FactoryMES-high-orders-screen.png'),
      fullPage: true
    });
    check('Desktop captures: alarms, inbox, orders (no page overflow)');

    // 6. 업무폰 폭(390px): 본문 가로 넘침 없음, 메뉴 이동·입력 조작
    const phone = await login(browser, 'portal', 'portal_password', {
      width: 390,
      height: 844
    });

    // 로그인하면 바로 메인 현황판(경보·알림·작업 3칸)이 보인다.
    await phone.locator('#dashboard .dash-panel').nth(2).waitFor();
    await noPageOverflow(phone, 'Phone dashboard');

    // 링크로 연 경보 상세는 팝업이다. 닫은 뒤 좁은 화면의 "메뉴" 버튼으로 업무 메뉴를 연다.
    await open(phone, '/alarms?alarmId=' + alarm.alarmId, '경보');
    const phoneDetail = phone.locator('section.alarm-detail');
    await phoneDetail.waitFor();
    await noPageOverflow(phone, 'Phone alarms');
    await button(phoneDetail, '닫기').click();
    await phoneDetail.waitFor({ state: 'detached' });
    await button(phone.locator('header.shell-top'), '메뉴').click();
    await phone.locator('nav.shell-menu').getByRole('link', { name: '알림함' }).click();
    await phone.getByRole('heading', {
      name: '알림함',
      exact: true
    }).waitFor();
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone inbox');
    await phone.getByLabel('읽지 않은 알림만').check();
    await status(phone, '완료했습니다.');
    await phone.getByLabel('읽지 않은 알림만').uncheck();
    await status(phone, '완료했습니다.');
    await open(phone, '/orders', '작업지시');
    await noPageOverflow(phone, 'Phone orders');
    await phone.screenshot({
      path: path.join(output, 'FactoryMES-high-phone-orders.png'),
      fullPage: true
    });
    check('Phone width 390: dashboard after login, popup closes, header menu and filter work');

    // 업무 화면 머리띠의 로그아웃은 한 번에 로그아웃하고 로그인 화면으로 돌아간다.
    await button(phone.locator('header.shell-top'), '로그아웃').click();
    await phone.locator('#login-form').waitFor({ state: 'visible' });
    const after = await phone.evaluate(
      async () => (await fetch('/api/v1/auth/me', { cache: 'no-store' })).status
    );
    if (after !== 401) {
      throw new Error('Session still active after header logout: ' + after);
    }
    check('Header logout signs out in one step and shows login form');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      testKey: key,
      alarmId: alarm.alarmId,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      fakeTelemetry: false,
      note: 'Fixture connection/rule disabled and order cancelled; history retained. '
        + 'Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-high-alarms-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser HIGH alarms and console UI verified');
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
    // 정리: 자체 fixture만 중지·취소한다(삭제하지 않고 이력은 남긴다).
    // 규칙을 먼저 끄면 매핑 변경 제한이 풀리지만, 여기서는 매핑을 바꾸지 않고 연결만 중지한다.
    if (admin) {
      try {
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
        if (targetCreated) {
          const current = await api(admin, 'GET', '/api/v1/integration-targets/' + key);
          if (current.status === 200 && current.json.enabled) {
            await api(admin, 'PUT', `/api/v1/integration-targets/${key}/state`, {
              expectedVersion: current.json.version,
              expectedConfigVersion: current.json.configVersion,
              enabled: false,
              reason: 'Browser HIGH verification complete'
            });
          }
        }
        if (order) {
          const current = await api(admin, 'GET', '/api/v1/orders/' + order.orderId);
          if (current.status === 200 && current.json.status === 'RELEASED') {
            await api(admin, 'POST', `/api/v1/orders/${order.orderId}/cancel`, {
              expectedVersion: current.json.version,
              reason: 'Browser HIGH verification complete'
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
