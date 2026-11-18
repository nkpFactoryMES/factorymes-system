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
    const products = await get(admin, '/api/v1/products?limit=100');
    const profile = await get(worker, '/api/v1/auth/me');
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
    const title = '재검토·조회 회귀 ' + Date.now();
    let order = await post(
      admin,
      '/api/v1/orders',
      {
        productId: products.items.find(item => item.code === 'DEMO-EA').productId,
        equipmentId: isolated.equipmentId,
        assigneeId: profile.userId,
        operationLabel: title,
        targetQty: 10,
        countMode: 'MANUAL',
        counterMappingId: null,
        plannedStart: new Date(now.getTime() - 60000).toISOString(),
        plannedEnd: new Date(now.getTime() + 3600000).toISOString(),
        checklist: []
      }
    );
    const route = '/api/v1/orders/' + order.orderId;
    order = await post(
      admin,
      route + '/release',
      {
        expectedVersion: order.version
      }
    );
    order = await post(
      worker,
      route + '/start',
      {
        expectedVersion: order.version
      }
    );
    const time = (await token(worker)).time;
    const saved = await post(
      worker,
      route + '/entries',
      {
        expectedVersion: order.version,
        entries: [entry(
          'TOTAL',
          10,
          time
        ), entry(
          'REJECT',
          2,
          time
        )]
      }
    );
    order = await post(
      worker,
      route + '/submit',
      {
        expectedVersion: saved.version
      }
    );

    let reads = 0;
    await admin.page.route('**/api/v1/orders/*/corrections*', async route => {
      if (route.request().method() === 'GET') {
        reads++;
        if (reads === 1) {
          await route.fulfill({
            status: 503,
            contentType: 'application/problem+json',
            body: JSON.stringify({
              title: '일시적 정정 조회 실패',
              status: 503,
              code: 'TestUnavailable',
              correlationId: 'browser-regression'
            })
          });
          return;
        }
      }
      await route.fallback();
    });
    await admin.page.goto('https://localhost:7443/orders');
    await admin.page.getByRole('row').filter({ hasText: title }).getByRole('button', {
      name: '상세',
      exact: true
    }).click();
    await admin.page.waitForFunction(() => document
      .querySelector('section.operations [role="status"]')?.textContent
      .includes('일시적 정정 조회 실패'));
    check(reads === 1, 'Correction fetch failure is shown');
    const retry = admin.page.waitForResponse(response =>
      response.url().includes('/corrections') && response.request().method() === 'GET'
        && response.status() === 200);
    await admin.page.getByRole('button', {
      name: '새로고침',
      exact: true
    }).click();
    await retry;
    const unchanged = await get(admin, route);
    check(reads >= 2 && unchanged.version === order.version,
      'Same-version parent refresh retries failed correction query');

    // 정정 요청·확인은 작업 상세의 "중지·정정" 탭에 있다.
    await admin.page.getByRole('tablist', { name: '작업 상세' })
      .getByRole('tab', {
        name: '중지·정정',
        exact: true
      })
      .click();
    await admin.page.getByLabel('정정 요청 사유').fill('재검토 회귀');
    await admin.page.getByRole('button', {
      name: '정정 요청',
      exact: true
    }).click();
    await admin.page.getByLabel('확인 메모', { exact: false }).waitFor();
    const correction = (await get(admin, route + '/corrections')).items[0];
    const reject = saved.entries.find(item => item.kind === 'REJECT');
    const correctionTime = (await token(worker)).time;
    await post(
      worker,
      route + '/entries',
      {
        expectedVersion: correction.orderVersion,
        entries: [
          entry(
            'REVERSAL',
            2,
            correctionTime,
            correction.correctionId,
            reject.entryId
          ),
          entry(
            'REJECT',
            1,
            correctionTime,
            correction.correctionId
          )
        ]
      }
    );
    await admin.page.getByLabel('확인 메모', { exact: false }).fill('검토 확인');
    await admin.page.getByRole('button', {
      name: '정정 확인 완료',
      exact: true
    }).click();
    await admin.page.waitForFunction(() => document
      .querySelector('section.operations [role="status"]')?.textContent
      .includes('실적이 변경되었습니다'));
    const stillOpen = (await get(admin, route + '/corrections')).items[0];
    check(stillOpen.status === 'OPEN', 'Stale review does not resolve unseen changes');
    await admin.page.getByText('양품 9', { exact: false }).waitFor();
    check(true, 'Stale review refreshes current ledger for explicit review');
    await admin.page.getByRole('button', {
      name: '정정 확인 완료',
      exact: true
    }).click();
    await admin.page.getByText('RESOLVED', { exact: true }).waitFor();
    check(true, 'Second explicit review resolves current correction');
    // 관리자 마감은 "기본" 탭에 있다.
    await admin.page.getByRole('tablist', { name: '작업 상세' })
      .getByRole('tab', {
        name: '기본',
        exact: true
      })
      .click();
    await admin.page.getByLabel('마감 검토 사유').fill('회귀 확인 후 마감');
    await admin.page.getByRole('button', {
      name: '관리자 마감',
      exact: true
    }).click();
    await admin.page.getByText(order.orderNo + ' · CLOSED', {
      exact: false
    }).waitFor();
    check((await get(admin, route)).status === 'CLOSED', 'Review fixture closed normally');
    await admin.page.screenshot({
      path: path.join(project, 'verification-results/FactoryMES-lifecycle-closed.png'),
      fullPage: true
    });
    fs.writeFileSync(path.join(project, 'verification-results/lifecycle-review-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          cloudApiCalls: 0,
          externalDeliveryCalls: 0
        },
        null,
        2
      ));
  } finally {
    await browser.close();
  }
})().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
