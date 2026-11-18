// Modbus TCP 설비 수집을 실제 modbus-simulator와 관리자 화면으로 확인한다.
//   fixture(API): 고유 라인·설비 2대, 비어 있는 Modbus unit 하나(다른 연결이 쓰지 않는 번호)
//   → 관리자 화면: 새 연결 → HTTP 입력 후 Modbus로 바꿨다 되돌려도 입력이 보존되는지
//   → Modbus 예시 매핑(counter 0 UINT32 ABCD / 온도 2 INT16 AB ×0.1) 표시, 잘못된 순서는 보내기 전 거절
//   → 태그 추가로 FLOAT32·UINT32 워드 순서·INT16 부호 확인용 태그를 더해 등록(커넥터 종류 자동 선택)
//   → 설정 적용·CONNECTED → 실제 FC03 값 해석 확인(카운터 증가, 25~34℃, 123.5, 0x12345678, -123)
//   → 화면에서 배율 변경 저장 → v2 값에 반영, v1 이력 유지
//   → 같은 unit을 다른 설비에 화면으로 등록하면 409, 원인 문구 표시·입력 보존
//   → AUTO 작업(API): 시작 후 Modbus 카운터 증가분만 AUTO TOTAL로 쌓이고 제출·마감
// 유료·외부 호출 없음(브라우저는 localhost만). 실행: GPT가 modbus-simulator·Backend·Web 배포를 마친 뒤
// `node verify-modbus-collection.cjs`. HTTP Mock 수집 회귀는 verify-collection.cjs(GPT)가 맡는다.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'MB-' + crypto.randomUUID().slice(0, 8).toUpperCase();
const otherKey = key + '-B';

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
    throw new Error(
      `${label}: ${method} ${url} HTTP ${result.status} ${JSON.stringify(result.json)}`
    );
  }
  return result.json;
}

async function waitUntil(action, condition, label, timeoutMs = 60000) {
  const deadline = Date.now() + timeoutMs;
  let last = null;
  while (Date.now() < deadline) {
    last = await action();
    if (condition(last)) {
      return last;
    }
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  throw new Error(label + ' (last: ' + JSON.stringify(last)?.slice(0, 300) + ')');
}

// 팝업 안 상태 문구(처리 중이 끝난 뒤의 값).
async function dialogStatus(page) {
  await page.waitForFunction(() => !document.querySelector('main > fieldset')?.disabled);
  return page.locator('.modal-status').textContent();
}

// 다른 연결이 쓰지 않는 Modbus unit을 고른다(같은 unit 이중 등록은 Backend가 409로 막는다).
async function freeUnit(page) {
  const used = new Set();
  let cursor = null;
  do {
    const list = await expectStatus(
      page,
      'GET',
      '/api/v1/integration-targets?limit=100' + (cursor ? '&cursor=' + cursor : ''),
      undefined,
      200,
      'Targets'
    );
    for (const target of list.items) {
      const match = /^modbus\.tcp:\/\/modbus-simulator:1502\/unit\/(\d+)$/
        .exec(target.config.content.settings.approvedEndpoint);
      if (match && !target.deleted) {
        used.add(Number(match[1]));
      }
    }
    cursor = list.nextCursor;
  } while (cursor);

  for (let unit = 247; unit >= 1; unit--) {
    if (!used.has(unit)) {
      return unit;
    }
  }
  throw new Error('No free Modbus unit left (1..247 all bound)');
}

function metric(sample, code) {
  const found = sample.metrics.find(x => x.metricCode === code);
  if (!found) {
    throw new Error(`Metric ${code} missing in sample ${sample.telemetryId}`);
  }
  if (found.quality !== 'GOOD') {
    throw new Error(`Metric ${code} quality ${found.quality}`);
  }
  return Number(found.numericValue);
}

// 태그 한 줄(Modbus 입력)을 채운다. index는 화면의 "태그 N" 번호다.
async function fillTag(dialog, index, values) {
  const tag = '태그 ' + index;
  if (values.code !== undefined) {
    await dialog.getByLabel(`지표 코드 (${tag})`).fill(values.code);
  }
  if (values.source !== undefined) {
    await dialog.getByLabel(`소스 주소 (${tag})`).fill(values.source);
  }
  if (values.type !== undefined) {
    await dialog.getByLabel(`데이터 타입 (${tag})`).selectOption(values.type);
  }
  if (values.unit !== undefined) {
    await dialog.getByLabel(`단위 (${tag})`).fill(values.unit);
  }
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });

  let admin = null;
  const targets = [];
  const orders = [];
  let unit = null;

  try {
    admin = await login(browser, 'admin', 'admin_password');
    const worker = await login(browser, 'portal', 'portal_password');

    // 0. fixture: 고유 라인·설비 2대와 비어 있는 unit
    const me = await expectStatus(worker, 'GET', '/api/v1/auth/me', undefined, 200, 'Worker me');
    const line = await expectStatus(
      admin,
      'POST',
      '/api/v1/lines',
      {
        code: key,
        name: 'Modbus 검증 라인 ' + key,
        active: true
      },
      201,
      'Create line'
    );
    const equipment = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: key,
        name: 'Modbus 검증 설비 ' + key,
        lineId: line.lineId,
        active: true
      },
      201,
      'Create equipment'
    );
    const otherEquipment = await expectStatus(
      admin,
      'POST',
      '/api/v1/equipment',
      {
        code: otherKey,
        name: 'Modbus 중복 unit 검증 설비 ' + otherKey,
        lineId: line.lineId,
        active: true
      },
      201,
      'Create second equipment'
    );
    unit = await freeUnit(admin);
    const endpoint = 'modbus.tcp://modbus-simulator:1502/unit/' + unit;
    check(`Fixture: unique line/equipment and free Modbus unit ${unit}`);

    // 1. 새 연결 팝업: HTTP 입력 → Modbus 전환 → HTTP 복귀 시 입력 보존
    await admin.goto(base + '/equipment-connections');
    await button(admin.locator('main .toolbar'), '새 연결').click();
    const dialog = admin.getByRole('dialog', { name: '새 연결 등록' });
    await dialog.waitFor();
    await dialog.getByLabel('연결 ID', { exact: true }).fill(key);
    await dialog.getByLabel('연결 표시명', { exact: true }).fill('Modbus 검증 연결 ' + key);
    await dialog.getByLabel('연결 설비').selectOption(equipment.equipmentId);
    await dialog.getByLabel('배율 (temperature)', { exact: true }).fill('3');

    await dialog.getByLabel('프로토콜').selectOption('MODBUS_TCP');
    const modbusEndpoint = dialog.getByLabel('승인된 Modbus 주소');
    await modbusEndpoint.waitFor();
    if (await modbusEndpoint.inputValue() !== 'modbus.tcp://modbus-simulator:1502/unit/1'
      || await dialog.getByLabel('소스 주소 (태그 1)').inputValue() !== 'holding:0:UINT32:ABCD'
      || await dialog.getByLabel('소스 주소 (태그 2)').inputValue() !== 'holding:2:INT16:AB'
      || await dialog.getByLabel('배율 (태그 2)').inputValue() !== '0.1'
      || await dialog.getByLabel('지표 종류 (태그 1)').inputValue() !== 'COUNTER') {
      throw new Error('Modbus example endpoint/mappings were not filled');
    }
    check('Modbus mode shows example endpoint and counter/temperature register mappings');

    await dialog.getByLabel('프로토콜').selectOption('HTTP');
    const httpScale = dialog.getByLabel('배율 (temperature)', { exact: true });
    await httpScale.waitFor();
    if (await httpScale.inputValue() !== '3'
      || await dialog.getByLabel('승인된 Mock URL').inputValue()
        !== 'http://equipment-simulator:8080/equipment/' + key) {
      throw new Error('HTTP input was not preserved across protocol switch');
    }
    await dialog.getByLabel('프로토콜').selectOption('MODBUS_TCP');
    await modbusEndpoint.waitFor();
    check('Switching protocol keeps each protocol draft (user input preserved)');

    // 2. 보내기 전 검증: 16비트 태그에 32비트 순서를 주면 요청 없이 거절
    let posts = 0;
    const countPosts = request => {
      if (request.method() === 'POST' && request.url().endsWith('/api/v1/integration-targets')) {
        posts++;
      }
    };
    admin.on('request', countPosts);
    await fillTag(dialog, 2, { source: 'holding:2:INT16:ABCD' });
    await button(dialog, '연결 등록').click();
    const refused = await dialogStatus(admin);
    admin.off('request', countPosts);
    if (posts !== 0 || !refused.includes('소스 주소')) {
      throw new Error(`Invalid Modbus tag must be refused before sending: ${posts}, ${refused}`);
    }
    await fillTag(dialog, 2, { source: 'holding:2:INT16:AB' });
    check('Invalid word order for INT16 is refused in the browser before sending');

    // 3. 해석 확인용 태그를 더하고 등록한다.
    await modbusEndpoint.fill(endpoint);
    await dialog.getByLabel('수집 주기 (ms)').fill('1000');
    await button(dialog, '태그 추가').click();
    await fillTag(dialog, 3, {
      code: 'FLOAT_PROBE',
      source: 'holding:4:FLOAT32:ABCD',
      type: 'DECIMAL'
    });
    await button(dialog, '태그 추가').click();
    await fillTag(dialog, 4, {
      code: 'WORD_PROBE',
      source: 'holding:6:UINT32:ABCD',
      type: 'INTEGER'
    });
    await button(dialog, '태그 추가').click();
    await fillTag(dialog, 5, {
      code: 'SIGNED_PROBE',
      source: 'holding:8:INT16:AB',
      type: 'INTEGER'
    });

    const registered = admin.waitForResponse(response =>
      response.request().method() === 'POST'
      && response.url().endsWith('/api/v1/integration-targets'));
    await button(dialog, '연결 등록').click();
    const response = await registered;
    const sent = response.request().postDataJSON();
    if (response.status() !== 201) {
      throw new Error(`Modbus registration HTTP ${response.status()} ${await response.text()}`);
    }
    targets.push(key);
    if (sent.connectorType !== 'equipment-modbus-tcp'
      || sent.settings.protocol !== 'MODBUS_TCP'
      || sent.settings.approvedEndpoint !== endpoint
      || sent.settings.mappings.length !== 5) {
      throw new Error('Registration body differs: ' + JSON.stringify(sent));
    }
    check('Admin UI registers Modbus target with matching connector type and 5 tags');

    // 저장하면 같은 팝업이 조회·수정 팝업으로 바뀐다(이름이 바뀐다).
    const edit = admin.getByRole('dialog', { name: '연결 조회·수정 · ' + key });
    await edit.waitFor();

    // 4. 실제 수집: 설정 적용·연결, FC03 값 해석
    await waitUntil(
      () => expectStatus(admin, 'GET', '/api/v1/integration-targets/' + key, undefined, 200, 'T'),
      x => x.connectionState === 'CONNECTED' && x.appliedConfigVersion === x.configVersion,
      'Modbus configuration did not apply / connect'
    );
    const samples = await waitUntil(
      () => expectStatus(
        admin,
        'GET',
        '/api/v1/integration-targets/' + key + '/samples',
        undefined,
        200,
        'Samples'
      ),
      x => x.items.length >= 3,
      'No Modbus samples collected'
    );
    const [newest, , oldest] = samples.items;
    const counterNew = metric(newest, 'TOTAL_COUNT');
    const counterOld = metric(oldest, 'TOTAL_COUNT');
    if (!(counterNew > counterOld) || counterOld < unit * 1000) {
      throw new Error(`Counter must grow from unit base: ${counterOld} -> ${counterNew}`);
    }
    check('UINT32 ABCD counter is read from FC03 and grows with simulator time');

    for (const sample of samples.items) {
      const temperature = metric(sample, 'TEMPERATURE');
      if (temperature < 25 || temperature > 34) {
        throw new Error('Temperature out of 25..34: ' + temperature);
      }
    }
    if (metric(newest, 'FLOAT_PROBE') !== 123.5
      || metric(newest, 'WORD_PROBE') !== 0x12345678
      || metric(newest, 'SIGNED_PROBE') !== -123) {
      throw new Error('Decoded probe values differ: ' + JSON.stringify(newest.metrics));
    }
    check('INT16 ×0.1 temperature, FLOAT32 123.5, UINT32 0x12345678 and INT16 -123 decode');

    await button(edit, '수집 현황 새로고침').click();
    await edit.locator('section.samples tbody tr').first().waitFor();
    await edit.locator('section.samples').getByText('FLOAT_PROBE', { exact: false }).first()
      .waitFor();
    check('Modbus samples render in administrator table');

    // 5. 저장 뒤 같은 팝업이 조회·수정으로 바뀐다. 배율 변경이 v2 값에 반영된다.
    // 저장된 매핑 순서는 서버가 정하므로 태그 번호 대신 지표 코드(legend)로 찾는다.
    // label 접근 이름은 앞 공백·입력값이 섞일 수 있어 ^ 고정 없이 부분 일치로 찾는다(행 안에 하나뿐).
    const row = code => edit.locator('fieldset.modbus-tag')
      .filter({ has: admin.locator('legend', { hasText: code }) });
    if (await edit.getByLabel('프로토콜').inputValue() !== 'MODBUS_TCP'
      || await row('SIGNED_PROBE').getByLabel(/소스 주소/).inputValue()
        !== 'holding:8:INT16:AB') {
      throw new Error('Saved Modbus settings were not reloaded into the edit form');
    }
    await row('TEMPERATURE').getByLabel(/배율/).fill('0.2');
    await edit.getByLabel('설정 변경 사유').fill('Verify Modbus scale change');
    const saved = admin.waitForResponse(r => r.request().method() === 'PUT'
      && r.url().endsWith('/' + key + '/configuration'));
    await button(edit, '연결 설정 저장').click();
    if ((await saved).status() !== 200) {
      throw new Error('Modbus configuration update refused');
    }
    const scaled = await waitUntil(
      () => expectStatus(
        admin,
        'GET',
        '/api/v1/integration-targets/' + key + '/samples?limit=50',
        undefined,
        200,
        'Samples'
      ),
      x => x.items.some(item => item.configVersion === 2
        && metric(item, 'TEMPERATURE') >= 50 && metric(item, 'TEMPERATURE') <= 68),
      'Scale change did not reach v2 Modbus measurements'
    );
    if (!scaled.items.some(item => item.configVersion === 1)) {
      throw new Error('v1 Modbus samples should remain as history');
    }
    check('Edited scale propagates to v2 Modbus measurements; v1 history kept');

    // 6. 같은 unit을 다른 설비에 화면으로 등록하면 409. 원인 문구가 보이고 입력은 그대로 남는다.
    await button(edit.locator('.modal-bar'), '닫기').click();
    await button(admin.locator('main .toolbar'), '새 연결').click();
    const duplicate = admin.getByRole('dialog', { name: '새 연결 등록' });
    await duplicate.waitFor();
    await duplicate.getByLabel('연결 ID', { exact: true }).fill(otherKey);
    await duplicate.getByLabel('연결 표시명', { exact: true }).fill('Modbus 중복 unit ' + otherKey);
    await duplicate.getByLabel('연결 설비').selectOption(otherEquipment.equipmentId);
    await duplicate.getByLabel('프로토콜').selectOption('MODBUS_TCP');
    await duplicate.getByLabel('승인된 Modbus 주소').fill(endpoint);
    const refusedPost = admin.waitForResponse(response =>
      response.request().method() === 'POST'
      && response.url().endsWith('/api/v1/integration-targets'));
    await button(duplicate, '연결 등록').click();
    const duplicateResponse = await refusedPost;
    if (duplicateResponse.status() === 201) {
      targets.push(otherKey);
    }
    const duplicateText = await dialogStatus(admin);
    if (duplicateResponse.status() !== 409
      || !duplicateText.includes('입력을 보존')
      || !duplicateText.includes('같은 Modbus unit')) {
      throw new Error(
        `Duplicate unit must be refused with its reason: HTTP ${duplicateResponse.status()}, `
          + duplicateText
      );
    }
    if (await duplicate.getByLabel('연결 ID', { exact: true }).inputValue() !== otherKey
      || await duplicate.getByLabel('승인된 Modbus 주소').inputValue() !== endpoint
      || await duplicate.getByLabel('소스 주소 (태그 1)').inputValue()
        !== 'holding:0:UINT32:ABCD') {
      throw new Error('Refused duplicate registration lost the entered values');
    }
    check('Same Modbus unit on a second equipment is refused (409) with reason; input kept');
    await button(duplicate.locator('.modal-bar'), '닫기').click();

    // 7. AUTO 작업: Modbus 카운터 증가분만 AUTO TOTAL로 쌓인다.
    const options = await waitUntil(
      () => expectStatus(
        admin,
        'GET',
        '/api/v1/auto-counter-options?equipmentId=' + equipment.equipmentId,
        undefined,
        200,
        'Counter options'
      ),
      found => found.length === 1 && found[0].latestValue !== null,
      'Modbus COUNTER mapping is not offered for AUTO'
    );
    const products = await expectStatus(
      admin,
      'GET',
      '/api/v1/products?active=true&limit=100',
      undefined,
      200,
      'Products'
    );
    const created = await expectStatus(
      admin,
      'POST',
      '/api/v1/orders',
      {
        productId: products.items.find(x => x.code === 'DEMO-EA').productId,
        equipmentId: equipment.equipmentId,
        assigneeId: me.userId,
        operationLabel: 'Modbus AUTO 검증 ' + key,
        targetQty: 5,
        countMode: 'AUTO',
        counterMappingId: options[0].mappingId,
        plannedStart: new Date().toISOString(),
        plannedEnd: new Date(Date.now() + 3600000).toISOString(),
        checklist: []
      },
      201,
      'Create Modbus AUTO order'
    );
    orders.push(created.orderId);
    await expectStatus(
      admin,
      'POST',
      `/api/v1/orders/${created.orderId}/release`,
      { expectedVersion: created.version },
      200,
      'Release'
    );
    const released = await expectStatus(
      worker,
      'GET',
      '/api/v1/orders/' + created.orderId,
      undefined,
      200,
      'Order'
    );
    await expectStatus(
      worker,
      'POST',
      `/api/v1/orders/${created.orderId}/start`,
      { expectedVersion: released.version },
      200,
      'Start'
    );
    const counted = await waitUntil(
      () => expectStatus(admin, 'GET', '/api/v1/orders/' + created.orderId, undefined, 200, 'O'),
      found => found.autoCounter && !found.autoCounter.awaitingBaseline && found.totalQty >= 2,
      'Modbus counter did not produce AUTO total'
    );
    const raw = Number(counted.autoCounter.latestRawCounter);
    if (counted.totalQty > raw - Number(counted.startCounter)) {
      throw new Error('AUTO total exceeds counter increase since baseline');
    }
    check('AUTO order counts only Modbus counter increments after the baseline');

    for (const [who, action, body] of [
      [worker, 'submit', { completionReason: 'Modbus AUTO 검증 종료' }],
      [admin, 'close', { reason: 'Modbus AUTO 검증 종료' }]
    ]) {
      const current = await expectStatus(
        who,
        'GET',
        '/api/v1/orders/' + created.orderId,
        undefined,
        200,
        'Order'
      );
      await expectStatus(
        who,
        'POST',
        `/api/v1/orders/${created.orderId}/${action}`,
        {
          expectedVersion: current.version,
          ...body
        },
        200,
        'AUTO ' + action
      );
    }
    check('Modbus AUTO order submits and closes');

    await admin.screenshot({
      path: path.join(output, 'FactoryMES-modbus-collection.png'),
      fullPage: true
    });

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-modbus-collection-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          runtimeErrors: errors,
          testKey: key,
          modbusUnit: unit,
          notCovered: 'Simulator restart/outage and HTTP Mock regression are verified by GPT.',
          cloudApiCalls: 0,
          externalDeliveryCalls: 0,
          note: 'Fixture targets disabled after test; orders, samples and history retained.'
        },
        null,
        2
      )
    );
    console.log('Browser Modbus collection verified');
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
    // 정리: 자체 fixture만 멈춘다(삭제하지 않고 이력은 남긴다).
    // 발행만 된 작업은 취소하고, 수집 연결은 끈다(진행 중 AUTO가 남으면 Backend가 끄기를 막을 수 있다).
    if (admin) {
      try {
        for (const orderId of orders) {
          const current = await api(admin, 'GET', '/api/v1/orders/' + orderId);
          if (current.status === 200 && current.json.status === 'RELEASED') {
            await api(admin, 'POST', `/api/v1/orders/${orderId}/cancel`, {
              expectedVersion: current.json.version,
              reason: 'Browser Modbus verification complete'
            });
          }
        }
        for (const targetId of targets) {
          const current = await api(admin, 'GET', '/api/v1/integration-targets/' + targetId);
          if (current.status === 200 && current.json.enabled) {
            const result = await api(
              admin,
              'PUT',
              `/api/v1/integration-targets/${targetId}/state`,
              {
                expectedVersion: current.json.version,
                expectedConfigVersion: current.json.configVersion,
                enabled: false,
                reason: 'Browser Modbus verification complete'
              }
            );
            if (result.status !== 200) {
              console.error(`CLEANUP: target ${targetId} still enabled HTTP ${result.status}`);
            }
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
