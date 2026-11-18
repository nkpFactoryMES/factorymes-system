const { request } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const project = path.resolve(__dirname, '../..');
  const context = await request.newContext({
    baseURL: 'https://localhost:7443',
    ignoreHTTPSErrors: true
  });
  const worker = await request.newContext({
    baseURL: 'https://localhost:7443',
    ignoreHTTPSErrors: true
  });
  const checks = [];
  async function csrf(client = context) {
    const response = await client.get('/api/v1/auth/csrf');
    return (await response.json()).requestToken;
  }

  try {
    const login = await context.post('/api/v1/auth/login', {
      headers: {
        'X-CSRF-TOKEN': await csrf()
      },
      data: {
        login: 'admin',
        password: fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets'), 'admin_password'), 'utf8').trim()
      }
    });
    if (!login.ok()) {
      throw new Error('Admin login failed: ' + login.status());
    }

    const workerLogin = await worker.post('/api/v1/auth/login', {
      headers: {
        'X-CSRF-TOKEN': await csrf(worker)
      },
      data: {
        login: process.env.MES_BROWSER_WORKER_LOGIN ?? 'worker.demo',
        password: fs.readFileSync(path.join(
          process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets'),
          process.env.MES_BROWSER_WORKER_SECRET ?? 'worker_password'
        ), 'utf8').trim()
      }
    });
    if (!workerLogin.ok()) {
      throw new Error('Worker login failed: ' + workerLogin.status());
    }

    const unknownId = '/api/v1/orders/9223372036854775807';
    const cases = [
      {
        name: 'Unknown submit field',
        actor: 'worker',
        url: unknownId + '/submit',
        body: {
          expectedVersion: 1,
          reason: 'not a submit field'
        },
        status: 400
      },
      {
        name: 'Unknown order creation field',
        url: '/api/v1/orders',
        body: { unexpected: true },
        status: 400
      },
      {
        name: 'Unknown login field',
        url: '/api/v1/auth/login',
        body: {
          login: 'unused',
          password: 'unused',
          unexpected: true
        },
        status: 400
      },
      {
        name: 'Unknown password-change field',
        url: '/api/v1/auth/password',
        body: {
          currentPassword: 'unused',
          newPassword: 'unused',
          unexpected: true
        },
        status: 400
      },
      {
        name: 'Wrong version JSON type',
        url: unknownId + '/release',
        body: { expectedVersion: 'not-a-number' },
        status: 400
      },
      {
        name: 'Malformed JSON',
        url: unknownId + '/release',
        body: '{"expectedVersion":',
        raw: true,
        status: 400
      },
      {
        name: 'Missing JSON body',
        url: unknownId + '/release',
        body: '',
        raw: true,
        status: 400
      },
      {
        name: 'Unsupported media type',
        url: unknownId + '/release',
        body: 'text payload',
        raw: true,
        contentType: 'text/plain',
        status: 415
      }
    ];
    for (const item of cases) {
      const actor = item.actor === 'worker' ? worker : context;
      const response = await actor.post(item.url, {
        headers: {
          'X-CSRF-TOKEN': await csrf(actor),
          'Content-Type': item.contentType ?? 'application/json'
        },
        data: item.raw ? item.body : JSON.stringify(item.body)
      });
      if (response.status() !== item.status) {
        throw new Error(item.name + ': expected ' + item.status + ', got ' + response.status());
      }
      if (!response.headers()['content-type']?.startsWith('application/problem+json')) {
        throw new Error(item.name + ': missing problem+json');
      }
      const problem = await response.json();
      const allowed = ['type', 'title', 'status', 'detail', 'instance', 'code', 'correlationId', 'errors'];
      if (problem.status !== item.status || !problem.code || !problem.correlationId
          || !problem.type || !problem.title
          || Object.keys(problem).some(key => !allowed.includes(key))) {
        throw new Error(item.name + ': Problem shape does not match the contract');
      }
      checks.push(item.name);
      console.log('PASS ' + item.name);
    }

    const report = {
      passed: checks.length,
      checks,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0
    };
    const reportPath = path.join(project, 'verification-results/input-errors-verification.json');
    fs.writeFileSync(reportPath, JSON.stringify(
      report,
      null,
      2
    ));
  } finally {
    await context.dispose();
    await worker.dispose();
  }
})().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
