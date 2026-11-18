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
      throw new Error('Fixture command HTTP ' + response.status());
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
    const page = admin.page;
    const products = await get(admin, '/api/v1/products?active=true&limit=100');
    const machines = await get(admin, '/api/v1/equipment?active=true&limit=100');
    const workers = await get(admin, '/api/v1/users?role=WORKER&assignable=true&limit=100');
    const now = new Date((await token(admin)).time);
    const label = '초안 충돌 회귀 ' + Date.now();
    const input = {
      productId: products.items.find(x => x.code === 'DEMO-EA').productId,
      equipmentId: machines.items.find(x => x.code === 'DEMO-MANUAL').equipmentId,
      assigneeId: workers.items.find(x => x.login === 'portal').userId,
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
      input
    );
    await page.goto('https://localhost:7443/orders');
    const row = page.locator('tr').filter({
      hasText: label
    });
    await row.getByRole('button', {
      name: '상세',
      exact: true
    }).click();
    // 초안 수정·취소는 작업 상세의 "초안·취소" 탭에 있다.
    await page.getByRole('tablist', { name: '작업 상세' })
      .getByRole('tab', {
        name: '초안·취소',
        exact: true
      })
      .click();
    await page.getByRole('button', {
      name: '초안 수정 열기',
      exact: true
    }).waitFor();
    let failed = false;
    await page.route('**/api/v1/equipment?active=true*', async route => {
      if (!failed) {
        failed = true;
        await route.fulfill({
          status: 503,
          contentType: 'application/problem+json',
          body: JSON.stringify({
            title: 'Temporary fixture failure',
            status: 503
          })
        });
      } else {
        await route.continue();
      }
    });
    await page.getByRole('button', {
      name: '초안 수정 열기',
      exact: true
    }).click();
    await page.waitForFunction(() => document.querySelector(
      'section.draft-editor [role="status"]'
    )?.textContent.includes('서버 응답'));
    check(failed, 'Editor reference-load failure displayed');
    await page.getByRole('button', {
      name: '초안 수정 열기',
      exact: true
    }).click();
    const target = page.getByLabel('초안 목표 수량 (EA)', {
      exact: true
    });
    await target.waitFor();
    check(await page.locator('section.draft-editor select').nth(1).locator('option').count() > 0,
      'Reference retry populates equipment');
    check(await page.locator('section.draft-editor select').nth(2).locator('option').count() > 0,
      'Reference retry populates workers');
    await target.fill('26');
    const csrf = await token(admin);
    const response = await admin.request.put(
      'https://localhost:7443/api/v1/orders/' + order.orderId + '/draft',
      {
        headers: {
          'X-CSRF-TOKEN': csrf.token
        },
        data: {
          ...input,
          expectedVersion: order.version,
          targetQty: 21,
          checklist: [{
            seq: 1,
            label: '서버 변경 점검',
            required: false
          }]
        }
      }
    );
    check(response.ok(), 'Concurrent draft update saved');
    await page.getByRole('button', {
      name: '초안 변경 저장',
      exact: true
    }).click();
    const conflict = page.locator('section.draft-editor .conflict');
    await conflict.waitFor();
    await conflict.getByText('서버 변경 점검 (선택)', { exact: true }).waitFor();
    check(await target.inputValue() === '26', 'Conflict preserves local input');
    check((await conflict.textContent()).includes('서버 변경 점검')
      && (await conflict.textContent()).includes('선택')
      && (await conflict.textContent()).includes('담당 작업자'),
    'Conflict shows latest references and complete checklist');
    await page.getByRole('button', {
      name: '내 입력 유지(최신 버전 기준)',
      exact: true
    }).click();
    await page.getByRole('button', {
      name: '초안 변경 저장',
      exact: true
    }).click();
    await page.getByRole('button', {
      name: '초안 수정 열기',
      exact: true
    }).waitFor();
    order = await get(admin, '/api/v1/orders/' + order.orderId);
    check(order.targetQty === 26 && order.checklist.length === 0,
      'Explicit conflict reconciliation saved whole draft');
    await page.route('**/api/v1/orders/' + order.orderId + '/cancel', async route => {
      await route.fetch();
      await route.abort();
    });
    await page.getByLabel('취소 사유', {
      exact: true
    }).fill('Lost response verification');
    await page.getByRole('button', {
      name: '작업지시 취소',
      exact: true
    }).click();
    await page.waitForFunction(() => document.querySelector(
      'section.draft-editor [role="status"]'
    )?.textContent.includes('결과를 확인'));
    order = await get(admin, '/api/v1/orders/' + order.orderId);
    check(order.status === 'CANCELLED', 'Unknown cancellation response committed once');
    await page.getByRole('button', {
      name: '새로고침',
      exact: true
    }).click();
    await page.waitForFunction(() => !document.querySelector('section.draft-editor'));
    check(true, 'Refresh reconciles cancelled order and removes controls');
    fs.writeFileSync(path.join(project, 'verification-results/master-review.json'),
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
