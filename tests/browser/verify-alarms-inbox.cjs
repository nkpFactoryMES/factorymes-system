// COMM_LOST 경보 규칙·경보 조회/확인·공통 알림함 화면을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-COMM_LOST-알림함-동시구현분담-v1.md의 "Claude 브라우저검증"과
// 2026-11-14 16:34 GPT 요청(결정적 fixture). 흐름은 Tooling AlarmVerification과 같다.
//   고유 설비 + portal 담당 RELEASED 작업 + 수집 주기 60초·만료 2초 연결
//   → 첫 실제 측정 뒤 데이터가 끊긴 것으로 판정 → Quartz가 경보 ACTIVE·알림 생성
//   → (화면) 작업자 알림함 → 관련 경보 링크 → ACK → 읽음 표시
//   → 수집 주기 1초·만료 20초로 바꿔 실제 측정 재개 → Quartz가 RECOVERED
//   → 정리: 연결·규칙 중지, 작업 취소(이력은 보존). 기존 자료는 건드리지 않는다.
// 실행: GPT 배포 뒤 `node verify-alarms-inbox.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'ALM-' + crypto.randomUUID().slice(0, 8).toUpperCase();

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

async function openAlarms(page, query = '') {
  await page.goto(base + '/alarms' + query);
  await page.getByRole('heading', {
    name: '경보',
    exact: true
  }).waitFor();
  await status(page, '완료했습니다.');
}

// 관리자 경보 화면의 "경보 규칙" 탭. 규칙 목록과 등록·수정 팝업은 이 탭 안에 있다.
async function showRulesTab(page) {
  await page.getByRole('tab', {
    name: '경보 규칙',
    exact: true
  }).click();
  await page.locator('section.rules').waitFor();
}

// 상단 "규칙 등록"으로 빈 등록 팝업을 연다.
async function openRuleCreate(page) {
  await button(page.locator('main .toolbar'), '규칙 등록').click();
  await page.getByRole('dialog', { name: '규칙 등록' }).waitFor();
}

async function openInbox(page) {
  await page.goto(base + '/inbox');
  await page.getByRole('heading', {
    name: '알림함',
    exact: true
  }).waitFor();
  await status(page, '완료했습니다.');
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

    // 0. fixture: 고유 설비, portal 담당 RELEASED 작업(작업자 경보 범위), 장주기·짧은 만료 연결
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
        name: '경보 검증 설비 ' + key,
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
        operationLabel: '경보 검증 ' + key,
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
        displayName: '경보 검증 연결 ' + key,
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
    check('Fixture: equipment, assigned RELEASED order, long-poll connection');

    // 1. 관리자 화면: 규칙 등록 → 같은 연결 중복 등록은 409 안내
    await openAlarms(admin);
    await showRulesTab(admin);
    await openRuleCreate(admin);
    const rules = admin.locator('section.rules');
    const option = await rules.getByLabel('감시할 연결 대상')
      .locator('option')
      .filter({ hasText: key })
      .getAttribute('value');
    await rules.getByLabel('감시할 연결 대상').selectOption(option);
    await rules.getByLabel('경보 등급').selectOption('WARN');
    await button(rules, '규칙 저장').click();
    await status(admin, '완료했습니다.');
    const ruleRow = rules.getByRole('row').filter({ hasText: key });
    await ruleRow.filter({ hasText: 'WARN' }).waitFor();
    const ruleList = await expectStatus(
      admin,
      'GET',
      `/api/v1/alarm-rules?equipmentId=${equipment.equipmentId}`,
      undefined,
      200,
      'Rule list'
    );
    ruleId = ruleList.items[0].ruleId;
    check('Admin registers COMM_LOST rule from connection list');

    // 저장하면 팝업이 닫히므로 같은 연결로 다시 등록 팝업을 연다.
    await openRuleCreate(admin);
    await rules.getByLabel('감시할 연결 대상').selectOption(option);
    await button(rules, '규칙 저장').click();
    await status(admin, '충돌');
    await button(admin.getByRole('dialog', { name: '규칙 등록' }), '닫기').click();
    check('Second rule on same connection is refused (409 shown)');

    // 2. 실제 측정 뒤 데이터가 끊기면 Quartz가 경보를 연다
    const alarm = await waitUntil(
      () => api(admin, 'GET', `/api/v1/alarms?equipmentId=${equipment.equipmentId}&status=ACTIVE`),
      result => result.status === 200 && result.json.items.length === 1,
      'Quartz did not open COMM_LOST alarm'
    ).then(result => result.json.items[0]);
    check('Quartz opens COMM_LOST alarm after first real sample goes stale');

    // 3. 작업자: 알림함 미읽음 → 관련 경보 링크 → 상세 → ACK
    await openInbox(worker);
    await worker.getByLabel('읽지 않은 알림만').check();
    await status(worker, '완료했습니다.');
    const notice = worker.locator('.notifications li.unread').filter({
      has: worker.locator(`a[href="/alarms?alarmId=${alarm.alarmId}"]`)
    });
    await notice.waitFor();
    check('Assigned worker receives unread alarm notification');

    await notice.getByRole('link', { name: '관련 경보 보기' }).click();
    await worker.getByRole('heading', { name: '경보 상세 · #' + alarm.alarmId }).waitFor();
    await status(worker, '완료했습니다.');
    const detail = worker.locator('section.alarm-detail');
    await detail.getByText('상태: ACTIVE').waitFor();
    if (await worker.locator('section.rules').count()) {
      throw new Error('Worker must not see rule management');
    }
    check('Notification link opens alarm detail; worker has no rule UI');

    await button(detail, '경보 확인(ACK)').click();
    await status(worker, '완료했습니다.');
    await detail.getByText('확인(ACK): 사용자 #' + me.userId, { exact: false }).waitFor();
    if (await button(detail, '경보 확인(ACK)').count()) {
      throw new Error('ACK button must disappear after first ACK');
    }
    check('Assigned worker acknowledges alarm; first ACK recorded');

    // ACK는 알림 읽음을 대신하지 않는다
    await openInbox(worker);
    await worker.getByLabel('읽지 않은 알림만').check();
    await status(worker, '완료했습니다.');
    await notice.waitFor();
    await button(notice, '읽음 표시').click();
    await status(worker, '완료했습니다.');
    if (await notice.count()) {
      throw new Error('Read notification still listed under unread filter');
    }
    await worker.getByLabel('읽지 않은 알림만').uncheck();
    await status(worker, '완료했습니다.');
    await worker.locator('.notifications li.read')
      .filter({ has: worker.locator(`a[href="/alarms?alarmId=${alarm.alarmId}"]`) })
      .first()
      .waitFor();
    check('ACK did not mark notification read; explicit read moves it out of unread');
    await worker.screenshot({
      path: path.join(output, 'FactoryMES-inbox-screen.png'),
      fullPage: true
    });

    const denied = await api(worker, 'POST', '/api/v1/alarm-rules', {
      equipmentId: equipment.equipmentId,
      targetId: key,
      mappingId: null,
      kind: 'COMM_LOST',
      severity: 'INFO',
      triggerHigh: null,
      clearBelow: null,
      holdSec: null,
      clearHoldSec: null,
      enabled: true
    });
    if (denied.status !== 403) {
      throw new Error('Worker rule create returned ' + denied.status);
    }
    check('Worker rule create API is 403');

    // 4. 수집 주기를 되돌려 실제 측정이 다시 들어오면 Quartz가 복구한다
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
        reason: 'Restore regular collection',
        settings: settings(1000, 20)
      },
      200,
      'Restore collection'
    );
    await waitUntil(
      () => api(admin, 'GET', '/api/v1/alarms/' + alarm.alarmId),
      result => result.status === 200 && result.json.status === 'RECOVERED',
      'Quartz did not recover alarm'
    );
    await openAlarms(admin);
    await admin.getByLabel('경보 상태 필터').selectOption('RECOVERED');
    await status(admin, '완료했습니다.');
    const statuses = await admin.locator('section.alarms tbody tr td:nth-child(4)')
      .allTextContents();
    if (statuses.some(x => x.trim() !== 'RECOVERED')) {
      throw new Error('Status filter returned non-RECOVERED alarms');
    }
    await admin.locator('section.alarms tbody tr')
      .filter({ hasText: '#' + alarm.alarmId })
      .filter({ hasText: '확인됨' })
      .waitFor();
    check('Real samples recover alarm; RECOVERED filter shows it with ACK kept');

    // 5. 관리자 화면: 규칙 수정 중 다른 변경으로 409 → 최신 규칙 조회 1회 실패(500 주입)
    //    → 저장 차단·재조회 버튼 → 재조회 → 입력 보존·최신 버전 표시 → 직접 다시 저장
    await showRulesTab(admin);
    const rulesAgain = admin.locator('section.rules');
    const row = rulesAgain.getByRole('row').filter({ hasText: key });
    await button(row, '규칙 수정').click();
    await status(admin, '완료했습니다.');
    await rulesAgain.getByLabel('경보 등급').selectOption('CRITICAL');

    // 화면이 들고 있는 버전을 낡게 만든다(실제 API로 등급만 INFO로 변경).
    const before = await expectStatus(
      admin,
      'GET',
      '/api/v1/alarm-rules/' + ruleId,
      undefined,
      200,
      'Rule detail'
    );
    const changed = await expectStatus(
      admin,
      'PUT',
      '/api/v1/alarm-rules/' + ruleId,
      {
        expectedVersion: before.version,
        equipmentId: before.equipmentId,
        targetId: before.targetId,
        mappingId: before.mappingId,
        kind: before.kind,
        severity: 'INFO',
        triggerHigh: before.triggerHigh,
        clearBelow: before.clearBelow,
        holdSec: before.holdSec,
        clearHoldSec: before.clearHoldSec,
        enabled: true
      },
      200,
      'Concurrent rule change'
    );

    // 이 규칙의 최신 GET만 한 번 500으로 바꾼다. 서버는 그대로 둔다.
    let injected = 0;
    const rulePath = base + '/api/v1/alarm-rules/' + ruleId;
    await admin.route(rulePath, route => {
      if (route.request().method() === 'GET' && injected === 0) {
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
    });

    await button(rulesAgain, '규칙 저장').click();
    await status(admin, '최신 값도 읽지 못했습니다');
    await admin.unroute(rulePath);
    if (injected !== 1) {
      throw new Error('Latest rule GET was not attempted after 409');
    }
    if (!(await button(rulesAgain, '규칙 저장').isDisabled())) {
      throw new Error('Save must be blocked until latest rule is reloaded');
    }
    if (await rulesAgain.getByLabel('경보 등급').inputValue() !== 'CRITICAL') {
      throw new Error('Severity input must be kept after conflict');
    }
    await rulesAgain.locator('.rule-server')
      .filter({ hasText: '버전 ' + before.version + ')' })
      .waitFor();
    check('Rule 409 with failed latest GET blocks save and keeps input (no page error)');

    await button(rulesAgain, '최신 규칙 다시 조회').click();
    await status(admin, '최신 규칙을 다시 읽었습니다');
    await rulesAgain.locator('.rule-server')
      .filter({ hasText: '버전 ' + changed.version + ')' })
      .filter({ hasText: '등급 INFO' })
      .waitFor();
    if (await rulesAgain.getByLabel('경보 등급').inputValue() !== 'CRITICAL') {
      throw new Error('Severity input must survive manual reload');
    }
    if (await button(rulesAgain, '최신 규칙 다시 조회').count()) {
      throw new Error('Retry button must disappear after successful reload');
    }
    if (await button(rulesAgain, '규칙 저장').isDisabled()) {
      throw new Error('Save must be enabled after successful reload');
    }
    check('Manual reload shows latest server version and keeps chosen input');

    // 재검토 뒤 직접 저장(등급 CRITICAL·사용 중지) — 정리도 겸한다
    await rulesAgain.getByLabel('규칙 사용').uncheck();
    await button(rulesAgain, '규칙 저장').click();
    await status(admin, '완료했습니다.');
    await row.filter({ hasText: 'CRITICAL' }).filter({ hasText: '중지' }).waitFor();
    const saved = await expectStatus(
      admin,
      'GET',
      '/api/v1/alarm-rules/' + ruleId,
      undefined,
      200,
      'Saved rule'
    );
    if (saved.version !== changed.version + 1 || saved.severity !== 'CRITICAL' || saved.enabled) {
      throw new Error('Manual save did not apply on latest version: ' + JSON.stringify(saved));
    }
    ruleId = null;
    check('Admin saves reviewed rule on latest version (CRITICAL, disabled)');

    await admin.screenshot({
      path: path.join(output, 'FactoryMES-alarms-screen.png'),
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
      alarmId: alarm.alarmId,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'Fixture connection/rule disabled and order cancelled; history retained. '
        + 'Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-alarms-inbox-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser alarms and inbox verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const text = await page.locator('main > p[role="status"]')
          .textContent()
          .catch(() => 'none');
        console.error('PAGE STATUS: ' + text);
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
              reason: 'Browser alarm verification complete'
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
              reason: 'Browser alarm verification complete'
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
