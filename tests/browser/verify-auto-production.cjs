// AUTO 생산실적(설비 카운터 자동 집계)을 실제 Simulator 수집과 브라우저로 확인한다.
//   fixture: 고유 설비 + 1초 수집 연결(TOTAL_COUNT, COUNTER·EA·배율1·오프셋0)
//   → 관리자 화면: 작업지시 작성에서 AUTO·카운터 매핑 선택(매핑 없으면 저장 전 거절) → 발행
//   → 작업자: 점검·시작 → 첫 값은 기준값(생산량 0) → 증가분만 총수량, 상세가 새로고침 없이 갱신
//   → 실행 중 불량 입력: 409면 입력 보존·자동 재전송 없음 / 중지 중 불량 입력 저장
//   → 중지 동안 합산 0 → 재개 후 새 기준(중지 중 증가분을 넘기지 않음)
//   → 수동 TOTAL API 거절, 화면에 총수량 칸 없음, 제출 전 취소 후보에 자동 TOTAL 없음
//   → 제출 → 관리자 마감 → 같은 설비 새 AUTO 작업은 새 기준(이전 작업 수량을 넘기지 않음)
// 카운터 감소(리셋)로 생기는 CounterInvalid·기준 재확인 UI는 강제 리셋 수단이 없어 여기서 다루지 않는다
// (GPT Tooling fixture 담당). 실행: GPT가 Backend·Web 배포를 마친 뒤 `node verify-auto-production.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const key = 'AUT-' + crypto.randomUUID().slice(0, 8).toUpperCase();

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

async function statusText(page) {
  await page.waitForFunction(() => !document.querySelector('main > fieldset')?.disabled);
  return page.locator('main > p[role="status"]').textContent();
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

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function getOrder(page, orderId) {
  return expectStatus(page, 'GET', '/api/v1/orders/' + orderId, undefined, 200, 'Order');
}

async function allEntries(page, orderId) {
  const items = [];
  let cursor = null;
  do {
    const suffix = cursor ? '?cursor=' + cursor : '';
    const pageResult = await expectStatus(
      page,
      'GET',
      `/api/v1/orders/${orderId}/entries${suffix}`,
      undefined,
      200,
      'Entries'
    );
    items.push(...pageResult.items);
    cursor = pageResult.nextCursor;
  } while (cursor);
  return items;
}

// 원장 합계: TOTAL - (TOTAL을 취소한 REVERSAL). 자동 원장만 쌓이므로 화면·서버 총수량과 같아야 한다.
function ledgerTotal(entries) {
  const totals = new Map(entries.filter(x => x.kind === 'TOTAL').map(x => [x.entryId, x.qty]));
  let sum = 0;
  for (const entry of entries) {
    if (entry.kind === 'TOTAL') {
      sum += entry.qty;
    } else if (entry.kind === 'REVERSAL' && totals.has(entry.reversalOf)) {
      sum -= totals.get(entry.reversalOf);
    }
  }
  return sum;
}

async function detailTab(page, name) {
  await page.getByRole('tablist', { name: '작업 상세' })
    .getByRole('tab', {
      name,
      exact: true
    })
    .click();
}

async function openOrder(page, title) {
  await page.goto(base + '/orders');
  await status(page, '완료했습니다.');
  await page.getByRole('row')
    .filter({ hasText: title })
    .getByRole('button', {
      name: '상세',
      exact: true
    })
    .click();
  await page.getByRole('heading', { name: '작업 상세 · ' + title }).waitFor();
  await status(page, '완료했습니다.');
}

function collectionSettings() {
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
        metricCode: 'TOTAL_COUNT',
        sourcePath: 'totalCount',
        dataType: 'INTEGER',
        valueKind: 'COUNTER',
        unit: 'EA',
        scale: '1',
        offset: '0',
        enabled: true
      }
    ]
  };
}

// 작업자 화면에서 불량 1건을 저장한다. 정상 AUTO 누적은 작업 Version을 올리지 않으므로
// 수집이 계속되는 중에도 첫 클릭에 저장돼야 한다(409면 실패). 클릭당 요청 1개, TOTAL 미포함도 본다.
async function saveReject(worker, qty, reason) {
  await detailTab(worker, '실적·제출');
  await worker.getByLabel('불량 증가 (EA)').fill(String(qty));
  await worker.getByLabel('불량 사유', { exact: true }).fill(reason);
  const sent = [];
  const watch = request => {
    if (request.method() === 'POST' && request.url().endsWith('/entries')) {
      sent.push(request.postDataJSON());
    }
  };
  worker.on('request', watch);
  const answered = worker.waitForResponse(response =>
    response.request().method() === 'POST' && response.url().endsWith('/entries'));
  await button(worker, '실적 저장').click();
  const response = await answered;
  const text = await statusText(worker);
  await sleep(500);
  worker.off('request', watch);
  if (sent.length !== 1) {
    throw new Error('One click must send exactly one entry request, sent ' + sent.length);
  }
  if (sent[0].entries.some(x => x.kind === 'TOTAL')) {
    throw new Error('AUTO entry form must not send TOTAL: ' + JSON.stringify(sent[0]));
  }
  if (response.status() >= 400 || text !== '완료했습니다.') {
    throw new Error(`Reject save refused (HTTP ${response.status()}): ${text}`);
  }
}

// 작업자 명령(중지·재개·제출) 버튼을 누르고 응답을 기다린다.
// 정상 수집 중에도 409 없이 한 번에 처리돼야 한다(상시 409 회귀 검증).
async function command(page, scope, name, pathEnd) {
  const answered = page.waitForResponse(response =>
    response.request().method() === 'POST' && response.url().endsWith(pathEnd));
  await button(scope, name).click();
  const response = await answered;
  if (response.status() >= 400) {
    throw new Error(`${name}: HTTP ${response.status()} ${await response.text()}`);
  }
}

// API 명령: 최신 버전을 읽어 한 번 보낸다(수집이 계속돼도 409가 나면 안 된다).
async function apiCommand(page, orderId, action, body, label) {
  const current = await getOrder(page, orderId);
  return expectStatus(
    page,
    'POST',
    `/api/v1/orders/${orderId}/${action}`,
    {
      expectedVersion: current.version,
      ...body
    },
    200,
    label
  );
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });

  let admin = null;
  let targetCreated = false;
  const orders = [];
  const title = 'AUTO 검증 ' + key;
  const nextTitle = 'AUTO 다음 작업 ' + key;

  try {
    admin = await login(browser, 'admin', 'admin_password');
    const worker = await login(browser, 'portal', 'portal_password');

    // 0. fixture: 고유 설비와 1초 COUNTER 수집
    const me = await expectStatus(worker, 'GET', '/api/v1/auth/me', undefined, 200, 'Worker me');
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
        name: 'AUTO 검증 설비 ' + key,
        lineId: lines.items.find(x => x.code === 'DEMO-LINE').lineId,
        active: true
      },
      201,
      'Create equipment'
    );
    await expectStatus(
      admin,
      'POST',
      '/api/v1/integration-targets',
      {
        targetId: key,
        displayName: 'AUTO 검증 연결 ' + key,
        connectorType: 'equipment-http',
        enabled: true,
        kind: 'EQUIPMENT',
        equipmentId: equipment.equipmentId,
        settings: collectionSettings()
      },
      201,
      'Register 1s COUNTER collection'
    );
    targetCreated = true;
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
      'COUNTER mapping did not receive samples'
    );
    const mappingId = options[0].mappingId;
    check('Fixture: unique equipment with live 1s TOTAL_COUNT COUNTER collection');

    // 1. 관리자 화면: AUTO·카운터 매핑 선택. 매핑 없이 저장하면 보내기 전에 막는다.
    await admin.goto(base + '/orders');
    await status(admin, '완료했습니다.');
    await button(admin.locator('main .toolbar'), '작업지시 작성').click();
    const create = admin.locator('section.order-create');
    await create.waitFor();
    await create.getByLabel('품목', { exact: false }).selectOption({ label: '시연 부품 (DEMO-EA)' });
    await create.locator('form > label').filter({ hasText: /^\s*설비/ }).locator('select').selectOption(equipment.equipmentId);
    await create.getByLabel('담당 작업자').selectOption(me.userId);
    await create.getByLabel('작업명', { exact: true }).fill(title);
    await create.getByLabel('목표 수량 (EA)').fill('5');
    await create.getByLabel('집계 방식').selectOption('AUTO');
    const mapping = create.getByLabel('카운터 매핑');
    await mapping.waitFor();
    await button(create, '초안 저장').click();
    if (await mapping.evaluate(element => element.validity.valid)) {
      throw new Error('Missing counter mapping must fail native form validation');
    }
    check('AUTO without a counter mapping is refused before sending');

    await mapping.selectOption(mappingId);
    await create.locator('.auto-counter-latest').filter({ hasText: '최근 값' }).waitFor();
    await button(create, '초안 저장').click();
    await admin.getByRole('heading', { name: '작업 상세 · ' + title }).waitFor();
    await admin.locator('.count-mode-line').filter({ hasText: 'AUTO · 설비 카운터' }).waitFor();
    const created = (await expectStatus(
      admin,
      'GET',
      '/api/v1/orders?limit=25',
      undefined,
      200,
      'Orders'
    )).items.find(x => x.operationLabel === title);
    orders.push(created.orderId);
    if (created.countMode !== 'AUTO' || created.counterMappingId !== mappingId) {
      throw new Error('Saved order mode differs: ' + JSON.stringify(created));
    }
    check('Admin creates AUTO draft with the chosen COUNTER mapping');

    await detailTab(admin, '기본');
    await button(admin, '작업지시 발행').click();
    await status(admin, '완료했습니다.');

    // 2. 작업자: 점검·시작 → 첫 값은 기준값, 그다음 증가분만 총수량
    await openOrder(worker, title);
    await detailTab(worker, '기본');
    await worker.getByRole('checkbox').first().check();
    await status(worker, '완료했습니다.');
    await button(worker, '작업 시작').click();
    await status(worker, '완료했습니다.');
    const status1 = worker.locator('section.auto-counter .auto-counter-state');
    await status1.waitFor();
    const counted = await waitUntil(
      () => getOrder(admin, created.orderId),
      found => found.autoCounter && !found.autoCounter.awaitingBaseline && found.totalQty >= 2,
      'AUTO total did not start counting after baseline'
    );
    const entries1 = await allEntries(admin, created.orderId);
    const start = Number(counted.startCounter);
    const latest = Number(counted.autoCounter.latestRawCounter);
    if (ledgerTotal(entries1) !== counted.totalQty) {
      throw new Error(`Ledger ${ledgerTotal(entries1)} != total ${counted.totalQty}`);
    }
    if (counted.totalQty > latest - start) {
      throw new Error(`First counter value was counted: total ${counted.totalQty}, `
        + `start ${start}, latest ${latest}`);
    }
    if (entries1.some(x => x.kind === 'TOTAL' && x.source !== 'AUTO')) {
      throw new Error('AUTO order has a non-AUTO TOTAL entry');
    }
    check('First counter value is the baseline; only later increments become AUTO TOTAL');

    // 상세는 새로고침 없이 자동 갱신된다(3초 주기).
    await status1.filter({ hasText: '자동 합산 중' }).waitFor({ timeout: 15000 });
    await worker.waitForFunction(
      expected => Array.from(document.querySelectorAll('main section p'))
        .some(p => /총수량 (\d+)/.test(p.textContent)
          && Number(p.textContent.match(/총수량 (\d+)/)[1]) >= expected),
      counted.totalQty,
      { timeout: 15000 }
    );
    await detailTab(worker, '기본');
    await worker.locator('.entry-auto').filter({ hasText: '설비 자동' }).first().waitFor();
    check('Detail refreshes without reload; ledger marks AUTO entries as 설비 자동');

    // 3. 작업자 입력: 총수량 칸 없음, 수동 TOTAL API는 거절
    await detailTab(worker, '실적·제출');
    await worker.getByRole('heading', { name: '불량 추가', exact: false }).waitFor();
    if (await worker.getByLabel('총수량 증가 (EA)').count()) {
      throw new Error('AUTO entry form must not offer manual total');
    }
    const latestOrder = await getOrder(worker, created.orderId);
    const manualTotal = await api(worker, 'POST', `/api/v1/orders/${created.orderId}/entries`, {
      expectedVersion: latestOrder.version,
      entries: [{
        clientEntryId: crypto.randomUUID(),
        kind: 'TOTAL',
        qty: 3,
        reversalOf: null,
        correctionId: null,
        reason: null,
        occurredAt: new Date().toISOString()
      }]
    });
    if (manualTotal.status < 400 || manualTotal.status >= 500) {
      throw new Error('Manual TOTAL on AUTO order must be refused: ' + manualTotal.status);
    }
    check('AUTO order has no manual total input; manual TOTAL API is refused');

    // 실행 중(수집 계속) 불량 입력: 첫 클릭에 저장, 요청 1개, TOTAL 미포함.
    await saveReject(worker, 1, '실행 중 불량');
    check('Reject while RUNNING with live collection saves on first click (no 409)');

    // 4. 중지 동안 합산 0
    await detailTab(worker, '중지·정정');
    await worker.getByLabel('중지 사유').selectOption('MATERIAL');
    await worker.getByLabel('메모', { exact: false }).fill('AUTO 중지 검증');
    await command(worker, worker.locator('section.operations'), '작업 중지', '/pause');
    await button(worker, '작업 재개').waitFor();
    await status1.filter({ hasText: '자동 합산 안 함' }).waitFor({ timeout: 15000 });
    const paused = await getOrder(admin, created.orderId);
    await sleep(4000);
    const pausedLater = await getOrder(admin, created.orderId);
    if (pausedLater.totalQty !== paused.totalQty) {
      throw new Error(`Total moved while PAUSED: ${paused.totalQty} -> ${pausedLater.totalQty}`);
    }
    check('PAUSE while collecting succeeds on first click; PAUSED adds no AUTO production');

    // 중지 중 불량 입력도 저장된다(입력 폼은 그대로 AUTO 전용).
    await saveReject(worker, 1, '중지 중 불량');
    const withReject = await getOrder(admin, created.orderId);
    if (withReject.rejectQty !== 2) {
      throw new Error('Reject quantity should be 2: ' + withReject.rejectQty);
    }
    check('Worker records reject only (manual) on AUTO order');

    // 5. 재개 후 새 기준: 중지 중 늘어난 카운터는 생산량에 넣지 않는다.
    const beforeResume = await getOrder(admin, created.orderId);
    const rawAtResume = Number(beforeResume.autoCounter.latestRawCounter);
    const gap = rawAtResume - Number(paused.autoCounter.latestRawCounter);
    await detailTab(worker, '중지·정정');
    await command(worker, worker.locator('section.operations'), '작업 재개', '/resume');
    await button(worker, '작업 중지').waitFor();
    const resumed = await waitUntil(
      () => getOrder(admin, created.orderId),
      found => found.status === 'RUNNING' && !found.autoCounter.awaitingBaseline
        && found.totalQty > paused.totalQty,
      'AUTO did not resume counting after new baseline'
    );
    const rawNow = Number(resumed.autoCounter.latestRawCounter);
    if (gap < 2 || resumed.totalQty - paused.totalQty > rawNow - rawAtResume) {
      throw new Error(`Paused increments leaked: paused total ${paused.totalQty}, `
        + `resumed ${resumed.totalQty}, gap ${gap}, since resume ${rawNow - rawAtResume}`);
    }
    check(`Resume takes a new baseline; ${gap} counts during PAUSED were not added`);

    // 6. 제출 전 실적 취소 후보에는 수동 불량만 있고 자동 TOTAL은 없다.
    await detailTab(worker, '실적·제출');
    const reversal = worker.locator('section.pre-submit-reversal');
    await reversal.waitFor();
    const labels = await reversal.locator('li').allTextContents();
    if (labels.some(text => text.includes('TOTAL')) || !labels.some(t => t.includes('REJECT'))) {
      throw new Error('Reversal candidates must be manual REJECT only: ' + JSON.stringify(labels));
    }
    check('Pre-submit reversal offers manual REJECT only, never AUTO TOTAL');

    // 7. 제출 → 관리자 마감
    await worker.getByLabel('제출 사유 (목표 수량과 다르면 필수)').fill('AUTO 검증 제출');
    await command(worker, worker.locator('main'), '제출', '/submit');
    await waitUntil(
      () => getOrder(admin, created.orderId),
      found => found.status === 'SUBMITTED',
      'AUTO order was not submitted'
    );
    const submitted = await getOrder(admin, created.orderId);
    const entriesAtSubmit = await allEntries(admin, created.orderId);
    if (ledgerTotal(entriesAtSubmit) !== submitted.totalQty) {
      throw new Error('Submitted total differs from ledger');
    }
    await openOrder(admin, title);
    await detailTab(admin, '기본');
    await admin.getByLabel('마감 검토 사유').fill('AUTO 자동 집계·불량 확인');
    await button(admin, '관리자 마감').click();
    await status(admin, '완료했습니다.');
    const closed = await getOrder(admin, created.orderId);
    if (closed.status !== 'CLOSED' || closed.totalQty !== submitted.totalQty) {
      throw new Error('AUTO order did not close with submitted total');
    }
    check('SUBMIT while collecting succeeds on first click; admin close keeps ledger total');

    // 8. 같은 설비의 새 AUTO 작업은 새 기준(이전 작업·공백 수량을 넘기지 않는다)
    const second = await expectStatus(
      admin,
      'POST',
      '/api/v1/orders',
      {
        productId: created.productId,
        equipmentId: equipment.equipmentId,
        assigneeId: me.userId,
        operationLabel: nextTitle,
        targetQty: 3,
        countMode: 'AUTO',
        counterMappingId: mappingId,
        plannedStart: new Date().toISOString(),
        plannedEnd: new Date(Date.now() + 3600000).toISOString(),
        checklist: []
      },
      201,
      'Create next AUTO order'
    );
    orders.push(second.orderId);
    let next = await expectStatus(
      admin,
      'POST',
      `/api/v1/orders/${second.orderId}/release`,
      { expectedVersion: second.version },
      200,
      'Release next order'
    );
    await openOrder(worker, nextTitle);
    const before = await expectStatus(
      admin,
      'GET',
      '/api/v1/auto-counter-options?equipmentId=' + equipment.equipmentId,
      undefined,
      200,
      'Counter before next start'
    );
    const rawBeforeStart = Number(before[0].latestValue);
    await detailTab(worker, '기본');
    await button(worker, '작업 시작').click();
    await status(worker, '완료했습니다.');
    next = await waitUntil(
      () => getOrder(admin, second.orderId),
      found => found.autoCounter && !found.autoCounter.awaitingBaseline && found.totalQty >= 1,
      'Next AUTO order did not start counting'
    );
    const nextRaw = Number(next.autoCounter.latestRawCounter);
    if (next.totalQty > nextRaw - rawBeforeStart) {
      throw new Error(`Next order carried over counts: total ${next.totalQty}, `
        + `raw since start ${nextRaw - rawBeforeStart}`);
    }
    check('Next AUTO order on the same equipment starts from a new baseline');

    // 다음 작업도 제출·마감해 진행 중 작업을 남기지 않는다(API, 같은 업무 규칙).
    await apiCommand(
      worker,
      second.orderId,
      'submit',
      { completionReason: 'AUTO 다음 작업 검증 종료' },
      'Submit next order'
    );
    await apiCommand(
      admin,
      second.orderId,
      'close',
      { reason: 'AUTO 다음 작업 검증 종료' },
      'Close next order'
    );

    await admin.screenshot({
      path: path.join(output, 'FactoryMES-auto-production.png'),
      fullPage: true
    });

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-auto-production-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          runtimeErrors: errors,
          testKey: key,
          notCovered: 'Counter decrease (CounterInvalid) and rebase UI need a reset fixture.',
          cloudApiCalls: 0,
          externalDeliveryCalls: 0,
          note: 'Fixture collection disabled after test; orders and ledger history retained.'
        },
        null,
        2
      )
    );
    console.log('Browser AUTO production verified');
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
    // 진행 중인 다음 작업은 중지해 설비 점유를 남기지 않고, 수집 연결은 끈다.
    if (admin) {
      try {
        for (const orderId of orders) {
          const current = await api(admin, 'GET', '/api/v1/orders/' + orderId);
          if (current.status === 200 && current.json.status === 'RELEASED') {
            await api(admin, 'POST', `/api/v1/orders/${orderId}/cancel`, {
              expectedVersion: current.json.version,
              reason: 'Browser AUTO verification complete'
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
              reason: 'Browser AUTO verification complete'
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
