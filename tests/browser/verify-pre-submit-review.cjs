const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const project = path.resolve(__dirname, '../..');
  const checks = [];
  const contexts = [];
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });
  function check(condition, name) {
    if (!condition) {
      throw new Error(name);
    }
    checks.push(name);
    console.log('PASS ' + name);
  }

  async function signedIn(login, secret) {
    const context = await browser.newContext({
      ignoreHTTPSErrors: true
    });
    contexts.push(context);
    await context.route('**/*', route => new URL(route.request().url()).hostname === 'localhost'
      ? route.continue() : route.abort());
    const page = await context.newPage();
    await page.goto('https://localhost:7443');
    await page.locator('#login-form').waitFor({
      state: 'visible'
    });
    await page.locator('#login').fill(login);
    await page.locator('#password').fill(
      fs.readFileSync(path.join(
        process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets'),
        secret
      ), 'utf8').trim()
    );
    await page.getByRole('button', {
      name: '로그인',
      exact: true
    }).click();
    await page.locator('#workspace').waitFor({
      state: 'visible'
    });
    check(true, login + ' review fixture login');
    return {
      page,
      request: context.request
    };
  }

  async function token(client) {
    const response = await client.request.get('https://localhost:7443/api/v1/auth/csrf');
    return {
      token: (await response.json()).requestToken,
      time: response.headers()['x-mes-time'].replace(/(\.\d{6})\d+Z$/, '$1Z')
    };
  }

  async function get(client, route) {
    const response = await client.request.get('https://localhost:7443' + route);
    if (!response.ok()) {
      throw new Error('Fixture read HTTP ' + response.status());
    }
    return response.json();
  }

  async function post(
    client,
    route,
    data
  ) {
    const csrf = await token(client);
    const response = await client.request.post('https://localhost:7443' + route, {
      headers: {
        'X-CSRF-TOKEN': csrf.token
      },
      data
    });
    if (!response.ok()) {
      throw new Error('Fixture command ' + route + ' HTTP ' + response.status()
        + ': ' + await response.text());
    }
    return response.json();
  }

  function entry(
    kind,
    qty,
    time,
    correctionId = null,
    reversalOf = null
  ) {
    return {
      clientEntryId: crypto.randomUUID(),
      kind,
      qty,
      reversalOf,
      correctionId,
      reason: 'Review regression',
      occurredAt: time
    };
  }

  try {
    const admin = await signedIn('admin', 'admin_password');
    const worker = await signedIn('portal', 'portal_password');
    const page = worker.page;
    const profile = await get(worker, '/api/v1/auth/me');
    const products = await get(admin, '/api/v1/products?active=true&limit=100');
    const lines = await get(admin, '/api/v1/lines?active=true&limit=100');
    const isolated = await post(
      admin,
      '/api/v1/equipment',
      {
        code: 'REVIEW-' + crypto.randomUUID().slice(0, 12),
        name: '격리 경계 검증 설비',
        lineId: lines.items.find(x => x.code === 'DEMO-LINE').lineId,
        active: true
      }
    );
    const now = new Date((await token(admin)).time);
    const label = '역분개 응답 회귀 ' + Date.now();
    const body = {
      productId: products.items.find(x => x.code === 'DEMO-EA').productId,
      equipmentId: isolated.equipmentId,
      assigneeId: profile.userId,
      operationLabel: label,
      targetQty: 10,
      countMode: 'MANUAL',
      counterMappingId: null,
      plannedStart: new Date(now.getTime() - 60000).toISOString(),
      plannedEnd: new Date(now.getTime() + 3600000).toISOString(),
      checklist: []
    };
    let order = await post(
      admin,
      '/api/v1/orders',
      body
    );
    const pathName = '/api/v1/orders/' + order.orderId;
    order = await post(
      admin,
      pathName + '/release',
      {
        expectedVersion: order.version
      }
    );
    order = await post(
      worker,
      pathName + '/start',
      {
        expectedVersion: order.version
      }
    );
    await post(
      worker,
      pathName + '/entries',
      {
        expectedVersion: order.version,
        entries: [
          entry('TOTAL', 10, (await token(worker)).time),
          entry('REJECT', 2, (await token(worker)).time)
        ]
      }
    );
    let other = await post(
      admin,
      '/api/v1/orders',
      {
        ...body,
        operationLabel: label + ' other'
      }
    );
    other = await post(
      admin,
      '/api/v1/orders/' + other.orderId + '/release',
      {
        expectedVersion: other.version
      }
    );
    await page.goto('https://localhost:7443/orders');
    const ownRow = page.locator('tbody tr').filter({
      hasText: label
    }).filter({
      hasNotText: 'other'
    });
    await ownRow.getByRole('button', {
      name: '상세',
      exact: true
    }).click();
    // 제출 전 실적 취소는 작업 상세의 "실적·제출" 탭에 있다.
    await page.getByRole('tablist', { name: '작업 상세' })
      .getByRole('tab', {
        name: '실적·제출',
        exact: true
      })
      .click();
    const form = page.locator('section.pre-submit-reversal');
    await form.locator('input[type="checkbox"]').first().waitFor();
    check(await form.locator('input[type="checkbox"]').count() === 2,
      'Own original ledger entries offered for cancellation');
    const total = form.locator('li').filter({
      hasText: 'TOTAL 10 EA'
    }).getByRole('checkbox');
    await total.check();
    await form.getByLabel('취소 사유').fill('Invariant review');
    await form.getByRole('button', {
      name: '선택 실적 취소',
      exact: true
    }).click();
    await form.getByRole('status').filter({
      hasText: '취소가 거절'
    }).waitFor();
    check(await form.getByRole('button', {
      name: '같은 취소 요청 재전송·결과 확인'
    }).count() === 0,
      'Definitive 422 clears pending request for corrected selection');
    const before = await get(worker, pathName + '/entries');
    check(before.items.length === 2, 'Rejected partial cancellation preserves ledger');
    await total.check();
    await form.locator('li').filter({
      hasText: 'REJECT 2 EA'
    }).getByRole('checkbox').check();
    let firstRequest = null;
    let retryRequest = null;
    let forwarded = 0;
    await page.route('**' + pathName + '/entries', async route => {
      if (route.request().method() !== 'POST') return route.continue();
      const data = route.request().postDataJSON();
      if (!firstRequest) {
        firstRequest = data;
        await route.fetch();
        forwarded++;
        await route.abort();
      } else {
        retryRequest = data;
        await route.continue();
      }
    });
    await form.getByRole('button', {
      name: '선택 실적 취소',
      exact: true
    }).click();
    await form.getByRole('button', {
      name: '같은 취소 요청 재전송·결과 확인', exact: true
    }).waitFor();
    await form.getByRole('status').filter({
      hasText: '결과를 확인하지 못했습니다'
    }).waitFor();
    order = await get(worker, pathName);
    check(forwarded === 1 && order.totalQty === 0 && order.rejectQty === 0,
      'Lost response batch cancels total and reject atomically');
    order = await post(
      worker,
      pathName + '/submit',
      {
        expectedVersion: order.version,
        completionReason: 'All fixture originals cancelled'
      }
    );
    order = await post(
      admin,
      pathName + '/close',
      {
        expectedVersion: order.version,
        reason: 'Closed before pending replay'
      }
    );
    await page.locator('tbody tr').filter({
      hasText: label + ' other'
    })
      .getByRole('button', {
        name: '상세',
        exact: true
      }).click();
    await page.getByRole('heading', {
      name: '작업 상세 · ' + label + ' other',
      exact: true
    }).waitFor();
    await page.locator('main > p[role="status"]')
      .filter({ hasText: '완료했습니다.' }).waitFor();
    await form.waitFor({ state: 'visible' });
    check(await page.getByRole('tablist', { name: '작업 상세' })
      .getByRole('tab', { name: '실적·제출', exact: true }).count() === 0,
    'Pending cancellation stays visible even when the selected order has no entry tab');
    check(await form.getByRole('button', {
      name: '같은 취소 요청 재전송·결과 확인', exact: true
    }).count() === 1, 'Pending result survives switching to another work order');
    await form.getByRole('button', {
      name: '같은 취소 요청 재전송·결과 확인', exact: true
    }).click();
    await form.waitFor({
      state: 'hidden'
    });
    check(JSON.stringify(firstRequest) === JSON.stringify(retryRequest),
      'Replay preserves original order body and UUIDs');
    const after = await get(worker, pathName + '/entries');
    const closed = await get(worker, pathName);
    check(after.items.length === 4 && after.items.filter(x => x.kind === 'REVERSAL').length === 2,
      'Replay after close creates no duplicate reversal');
    check(closed.status === 'CLOSED' && closed.version === order.version,
      'Closed-state replay leaves version and state unchanged');
    await post(
      admin,
      '/api/v1/orders/' + other.orderId + '/cancel',
      {
        expectedVersion: other.version,
        reason: 'Review fixture complete'
      }
    );
    fs.writeFileSync(path.join(project, 'verification-results/pre-submit-review.json'),
      JSON.stringify(
        {
          checks,
          count: checks.length,
          orderId: order.orderId
        },
        null,
        2
      ));
  } finally {
    for (const context of contexts) await context.close();
    await browser.close();
  }
})().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
