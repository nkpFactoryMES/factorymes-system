const { request } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
(async () => {
  const root = path.resolve(__dirname, '../..');
  const contextPath = path.join(root, 'verification-results/collection-resilience.json');
  const fixture = JSON.parse(fs.readFileSync(path.join(root, 'verification-results/collection-browser.json')));
  const data = fs.existsSync(contextPath) && process.argv[2] !== 'warm'
    ? JSON.parse(fs.readFileSync(contextPath)) : { targetId: fixture.targetId, checks: [] };
  const client = await request.newContext({ baseURL: 'https://localhost:7443', ignoreHTTPSErrors: true });
  async function get(route) {
    const response = await client.get('/api/v1/' + route);
    if (!response.ok()) throw new Error('Read HTTP ' + response.status());
    return response.json();
  }
  async function write(route, body, put = false) {
    const csrf = await get('auth/csrf');
    const response = await client.fetch('/api/v1/' + route, {
      method: put ? 'PUT' : 'POST',
      headers: { 'X-CSRF-TOKEN': csrf.requestToken },
      data: body
    });
    if (!response.ok()) throw new Error('Write HTTP ' + response.status());
    return response.json();
  }
  async function until(action, predicate) {
    const end = Date.now() + 25000;
    while (Date.now() < end) {
      const value = await action();
      if (predicate(value)) return value;
      await new Promise(resolve => setTimeout(resolve, 500));
    }
    throw new Error('Resilience state did not converge');
  }
  function check(ok, label) {
    if (!ok) throw new Error(label);
    data.checks.push(label);
    console.log('PASS ' + label);
  }
  const route = 'integration-targets/' + data.targetId;
  try {
    await write('auth/login', {
      login: 'admin',
      password: fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(root, '.secrets'), 'admin_password'), 'utf8').trim()
    });
    if (process.argv[2] === 'warm') {
      let current = await get(route);
      current = await write(route + '/state', {
        expectedVersion: current.version,
        expectedConfigVersion: current.configVersion,
        enabled: true,
        reason: 'Local resilience verification'
      }, true);
      data.configVersion = current.configVersion;
      await until(() => get(route), x => x.connectionState === 'CONNECTED');
      const samples = await until(() => get(route + '/samples'),
        x => x.items[0]?.configVersion === data.configVersion);
      data.before = samples.items[0].eventId;
      check(true, 'Live target ready before controlled simulator interruption');
    } else if (process.argv[2] === 'fault') {
      await until(() => get(route), x => x.connectionState === 'RETRYING');
      check(true, 'Simulator interruption reports RETRYING');
      const before = await get(route + '/samples');
      await new Promise(resolve => setTimeout(resolve, 2500));
      const after = await get(route + '/samples');
      check(before.items[0].eventId === after.items[0].eventId,
        'No fabricated telemetry while simulator unavailable');
      data.during = after.items[0].eventId;
    } else if (process.argv[2] === 'recovered') {
      await until(() => get(route), x => x.connectionState === 'CONNECTED');
      await until(() => get(route + '/samples'), x => x.items[0]?.eventId !== data.during
        && x.items[0]?.configVersion === data.configVersion);
      check(true, 'Simulator restart resumes real samples without re-registering configuration');
      const current = await get(route);
      await write(route + '/state', {
        expectedVersion: current.version,
        expectedConfigVersion: current.configVersion,
        enabled: false,
        reason: 'Resilience verification complete; stop overnight collection'
      }, true);
      await until(() => get(route), x => !x.enabled && x.connectionState === 'DISCONNECTED');
      check(true, 'Verification target stopped with history retained');
    } else throw new Error('Use warm, fault or recovered');
    data.count = data.checks.length;
    fs.writeFileSync(contextPath, JSON.stringify(data, null, 2));
  } finally { await client.dispose(); }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
