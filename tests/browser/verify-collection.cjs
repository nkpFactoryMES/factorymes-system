const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

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

  const key = 'B' + crypto.randomUUID().replaceAll('-', '').slice(0, 10);
  let target;
  let admin;
  async function waitUntil(action, condition, label) {
    const deadline = Date.now() + 25000;
    while (Date.now() < deadline) {
      const result = await action();
      if (condition(result)) return result;
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    throw new Error(label);
  }
  try {
    admin = await signedIn('admin', 'admin_password');
    const worker = await signedIn('portal', 'portal_password');
    const lines = await get(admin, '/api/v1/lines?active=true&limit=100');
    const equipment = await post(
      admin,
      '/api/v1/equipment',
      {
        code: key,
        name: 'Mock 수집 시연 ' + key,
        lineId: lines.items.find(x => x.code === 'DEMO-LINE').lineId,
        active: true
      }
    );
    const page = admin.page;
    await page.goto('https://localhost:7443/equipment-connections');
    // 새 연결 등록은 상단 "새 연결" 버튼으로 여는 팝업이다. 저장하면 같은 팝업이 조회·수정으로 바뀐다.
    await page.locator('main .toolbar').getByRole('button', {
      name: '새 연결',
      exact: true
    }).click();
    await page.getByRole('dialog', { name: '새 연결 등록' }).waitFor();
    await page.getByRole('button', {
      name: '연결 등록',
      exact: true
    }).waitFor();
    await page.getByLabel('연결 ID', {
      exact: true
    }).fill(key);
    await page.getByLabel('연결 표시명', {
      exact: true
    }).fill('Mock 수집 시연 ' + key);
    await page.getByLabel('연결 설비').selectOption(equipment.equipmentId);
    await page.getByLabel('수집 주기 (ms)').fill('1000');
    const registered = page.waitForResponse(r => r.request().method() === 'POST'
      && r.url().endsWith('/api/v1/integration-targets') && r.status() === 201);
    await page.getByRole('button', {
      name: '연결 등록',
      exact: true
    }).click();
    await registered;
    target = await waitUntil(() => get(admin, '/api/v1/integration-targets/' + key),
      x => x.connectionState === 'CONNECTED' && x.appliedConfigVersion === x.configVersion,
      'Gateway/connector configuration did not apply');
    check(true, 'Administrator UI registration applies configuration and starts actual connector');
    const samples = await waitUntil(() => get(admin, '/api/v1/integration-targets/' + key + '/samples'),
      x => x.items.length >= 2, 'No automatic mock samples');
    const counters = samples.items.map(x => Number(x.metrics.find(m => m.metricCode === 'TOTAL_COUNT').numericValue));
    check(counters[0] > counters[1], 'Simulator counter grows through gateway connector and Oracle');
    check(samples.items[0].metrics.length === 3
      && samples.items[0].metrics.some(m => m.textValue === 'RUNNING' || m.textValue === 'IDLE'),
    'Numeric and state metrics visible in real sample response');
    await page.getByRole('button', {
      name: '수집 현황 새로고침',
      exact: true
    }).click();
    await page.locator('section.samples tbody tr').first().waitFor();
    check(true, 'Persisted samples render in administrator table');
    await page.getByLabel('배율 (temperature)', {
      exact: true
    }).fill('2');
    await page.getByLabel('설정 변경 사유').fill('Verify scaled temperature');
    const saved = page.waitForResponse(r => r.request().method() === 'PUT'
      && r.url().endsWith('/' + key + '/configuration') && r.status() === 200);
    await page.getByRole('button', {
      name: '연결 설정 저장',
      exact: true
    }).click();
    await saved;
    const scaled = await waitUntil(() => get(admin, '/api/v1/integration-targets/' + key + '/samples'),
      x => x.items.some(item => item.configVersion === 2
        && Number(item.metrics.find(m => m.metricCode === 'TEMPERATURE').numericValue) >= 50),
      'Updated Redis configuration did not affect measurements');
    check(true, 'Edited mapping scale propagates to real v2 measurements');
    check(scaled.items.some(item => item.configVersion === 1), 'Original v1 samples remain immutable history');
    await page.getByLabel('활성·중지 사유').fill('Finish live collection verification');
    await page.getByRole('button', {
      name: '수집 중지',
      exact: true
    }).click();
    await waitUntil(() => get(admin, '/api/v1/integration-targets/' + key),
      x => !x.enabled && x.connectionState === 'DISCONNECTED', 'Collection did not stop');
    const stopped = await get(admin, '/api/v1/integration-targets/' + key + '/samples');
    await new Promise(resolve => setTimeout(resolve, 2500));
    const stable = await get(admin, '/api/v1/integration-targets/' + key + '/samples');
    check(stopped.items[0].eventId === stable.items[0].eventId, 'Disabled target stops generating new samples');
    await page.getByLabel('활성·중지 사유').fill('Verify collection resume');
    await page.getByRole('button', {
      name: '수집 재개',
      exact: true
    }).click();
    await waitUntil(() => get(admin, '/api/v1/integration-targets/' + key + '/samples'),
      x => x.items[0].eventId !== stable.items[0].eventId && x.items[0].configVersion === 4,
      'Collection did not resume with new config');
    check(true, 'Re-enabling resumes collection under new configuration version');
    await worker.page.goto('https://localhost:7443/equipment-connections');
    await worker.page.getByText('설비 연결 관리는 관리자만 사용할 수 있습니다.').waitFor();
    check(await worker.page.getByRole('button', {
      name: '연결 등록',
      exact: true
    }).count() === 0,
      'Worker cannot access connection configuration form');
    const denied = await worker.request.get('https://localhost:7443/api/v1/integration-targets');
    check(denied.status() === 403, 'Worker connection administration API denied');
    await page.getByRole('button', {
      name: '수집 현황 새로고침',
      exact: true
    }).click();
    await page.locator('section.samples tbody tr').first().waitFor();
    await page.screenshot({
      path: path.join(project, 'verification-results/FactoryMES-collection-screen.png'),
      fullPage: true
    });
    fs.writeFileSync(path.join(project, 'verification-results/collection-browser.json'),
      JSON.stringify(
        {
          checks,
          count: checks.length,
          targetId: key,
          equipmentId: equipment.equipmentId
        },
        null,
        2
      ));
  } finally {
    if (target && admin) {
      const current = await get(admin, '/api/v1/integration-targets/' + key);
      if (current.enabled) {
        const csrf = await token(admin);
        const result = await admin.request.put('https://localhost:7443/api/v1/integration-targets/' + key + '/state', {
          headers: {
            'X-CSRF-TOKEN': csrf.token
          },
          data: {
            expectedVersion: current.version,
            expectedConfigVersion: current.configVersion,
            enabled: false,
            reason: 'Verification complete; preserve history without unattended collection'
          }
        });
        if (!result.ok()) throw new Error('Fixture stop HTTP ' + result.status());
      }
    }
    for (const context of contexts) await context.close();
    await browser.close();
  }
})().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
