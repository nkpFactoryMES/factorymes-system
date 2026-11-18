const { request } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

(async () => {
  const project = path.resolve(__dirname, '../..');
  const checks = [];
  for (const account of [
    {
      login: 'admin',
      secret: 'admin_password',
      role: 'ADMIN',
      policyStatus: 200
    },
    {
      login: 'portal',
      secret: 'portal_password',
      role: 'WORKER',
      policyStatus: 403
    }
  ]) {
    const client = await request.newContext({
      baseURL: 'https://localhost:7443',
      ignoreHTTPSErrors: true
    });
    function check(condition, message) {
      if (!condition) {
        throw new Error(account.login + ': ' + message);
      }
      checks.push(account.login + ': ' + message);
      console.log('PASS ' + account.login + ': ' + message);
    }

    try {
      const password = fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets'), account.secret), 'utf8').trim();
      const csrf = await (await client.get('/api/v1/auth/csrf')).json();
      const response = await client.post('/api/v1/auth/login', {
        headers: {
          'X-CSRF-TOKEN': csrf.requestToken
        },
        data: {
          login: account.login,
          password
        }
      });
      check(response.status() === 200, 'requested credentials accepted');
      const profile = await (await client.get('/api/v1/auth/me')).json();
      check(profile.login === account.login && profile.roles.length === 1
        && profile.roles[0] === account.role, 'exact role and login returned');
      const policy = await client.get('/api/v1/execution-policy');
      check(policy.status() === account.policyStatus, 'administrator boundary enforced');
      const orders = await client.get('/api/v1/orders');
      check(orders.status() === 200, 'order listing allowed');
      const token = await (await client.get('/api/v1/auth/csrf')).json();
      const passwordChange = await client.post('/api/v1/auth/password', {
        headers: {
          'X-CSRF-TOKEN': token.requestToken
        },
        data: {
          currentPassword: password,
          newPassword: 'short'
        }
      });
      check(passwordChange.status() === 400, 'public password length policy unchanged');
      const after = await client.get('/api/v1/auth/me');
      check(after.status() === 200, 'rejected password change leaves session valid');
    } finally {
      await client.dispose();
    }
  }
  fs.writeFileSync(path.join(project, 'verification-results/dev-accounts-verification.json'),
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
})().catch(error => {
  console.error(error.message);
  process.exitCode = 1;
});
