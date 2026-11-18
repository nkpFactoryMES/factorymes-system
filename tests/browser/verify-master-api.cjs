const { request } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

(async () => {
  const root = path.resolve(__dirname, '../..');
  const checks = [];
  const clients = [];
  const key = 'M' + crypto.randomUUID().replaceAll('-', '').slice(0, 12);
  const password = crypto.randomBytes(18).toString('hex');
  const baseURL = 'https://localhost:7443';
  function check(condition, label) {
    if (!condition) throw new Error(label);
    checks.push(label);
    console.log('PASS ' + label);
  }
  async function client() {
    const value = await request.newContext({
      baseURL,
      ignoreHTTPSErrors: true
    });
    clients.push(value);
    return value;
  }
  async function token(c) {
    const r = await c.get('/api/v1/auth/csrf');
    if (!r.ok()) throw new Error('CSRF HTTP ' + r.status());
    return (await r.json()).requestToken;
  }
  async function send(
    c,
    method,
    url,
    data
  ) {
    return c.fetch(url, {
      method,
      headers: {
        'X-CSRF-TOKEN': await token(c)
      },
      data
    });
  }
  async function read(
    r,
    status,
    label
  ) {
    check(r.status() === status, label + ' HTTP ' + r.status());
    return r.json();
  }
  async function get(c, url) {
    const r = await c.get(url);
    if (!r.ok()) throw new Error('GET HTTP ' + r.status() + ' ' + url);
    return r.json();
  }
  async function login(
    c,
    login,
    password
  ) {
    await read(
      await send(
        c,
        'POST',
        '/api/v1/auth/login',
        {
          login,
          password
        }
      ),
      200,
      'Login ' + login
    );
  }
  const admin = await client();
  const worker = await client();
  const guest = await client();
  let product, line, machine, user, order;
  const orders = [];
  async function put(
    route,
    data,
    status,
    label
  ) {
    return read(
      await send(
        admin,
        'PUT',
        '/api/v1/' + route,
        data
      ),
      status,
      label
    );
  }
  async function post(
    route,
    data,
    status,
    label
  ) {
    return read(
      await send(
        admin,
        'POST',
        '/api/v1/' + route,
        data
      ),
      status,
      label
    );
  }
  const productBody = (p, extra = {}) => ({
    expectedVersion: p.version,
    code: p.code,
    name: p.name,
    unit: p.unit,
    active: p.active,
    ...extra
  });
  const lineBody = (p, extra = {}) => ({
    expectedVersion: p.version,
    code: p.code,
    name: p.name,
    active: p.active,
    ...extra
  });
  const machineBody = (p, extra = {}) => ({
    expectedVersion: p.version,
    code: p.code,
    name: p.name,
    lineId: p.lineId,
    active: p.active,
    ...extra
  });
  const userBody = (p, extra = {}) => ({
    expectedVersion: p.version,
    name: p.name,
    roles: p.roles,
    email: p.email,
    active: p.active,
    ...extra
  });
  try {
    await login(
      admin,
      'admin',
      fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(root, '.secrets'), 'admin_password'), 'utf8').trim()
    );
    await login(
      worker,
      'portal',
      fs.readFileSync(path.join(process.env.MES_SECRET_DIRECTORY || path.join(root, '.secrets'), 'portal_password'), 'utf8').trim()
    );
    await read(
      await guest.get('/api/v1/lines'),
      401,
      'Guest denied'
    );
    await read(
      await send(
        worker,
        'POST',
        '/api/v1/lines',
        {
          code: key,
          name: key,
          active: true
        }
      ),
      403,
      'Worker cannot create master'
    );
    const noCsrf = await admin.post('/api/v1/lines', {
      data: {
        code: key,
        name: key,
        active: true
      }
    });
    await read(
      noCsrf,
      403,
      'Missing CSRF denied'
    );
    await post(
      'products',
      {
        code: key,
        name: key,
        unit: 'KG',
        active: true
      },
      422,
      'Only EA'
    );
    product = await post(
      'products',
      {
        code: key,
        name: key,
        unit: 'EA',
        active: true
      },
      201,
      'Product create'
    );
    await post(
      'products',
      {
        code: key,
        name: key,
        unit: 'EA',
        active: true
      },
      409,
      'Duplicate code'
    );
    line = await post(
      'lines',
      {
        code: key,
        name: key,
        active: true
      },
      201,
      'Line create'
    );
    machine = await post(
      'equipment',
      {
        code: key,
        name: key,
        lineId: line.lineId,
        active: true
      },
      201,
      'Equipment create'
    );
    await post(
      'users',
      {
        login: key,
        name: key,
        role: 'WORKER',
        email: null,
        initialPassword: 'short'
      },
      422,
      'Public user password minimum retained'
    );
    user = await post(
      'users',
      {
        login: key,
        name: key,
        role: 'WORKER',
        email: null,
        initialPassword: password
      },
      201,
      'User create'
    );
    check(!JSON.stringify(user).match(/password|securityStamp/i), 'User response has no secrets');
    const current = await get(admin, '/api/v1/auth/me');
    const admins = await get(admin, '/api/v1/users?role=ADMIN&active=true&limit=100');
    if (admins.items.length === 1 && !admins.nextCursor) {
      const me = await get(admin, '/api/v1/users/' + current.userId);
      const problem = await put(
        'users/' + me.userId,
        userBody(me, {
          active: false
        }),
        409,
        'Last administrator guard'
      );
      check(problem.code === 'LastAdministrator', 'LastAdministrator problem code');
    }
    for (const [route, item, id] of [
      ['products', product, product.productId], ['lines', line, line.lineId],
      ['equipment', machine, machine.equipmentId], ['users', user, user.userId]
    ]) {
      const detail = await get(admin, '/api/v1/' + route + '/' + id);
      check(detail.version === item.version, route + ' detail version');
      await read(
        await worker.get('/api/v1/' + route + '/' + id),
        403,
        route + ' worker detail denied'
      );
    }
    const concurrent = await Promise.all([
      send(
        admin,
        'PUT',
        '/api/v1/products/' + product.productId,
        productBody(product, {
          name: key + ' concurrent A'
        })
      ),
      send(
        admin,
        'PUT',
        '/api/v1/products/' + product.productId,
        productBody(product, {
          name: key + ' concurrent B'
        })
      )
    ]);
    check(concurrent.map(x => x.status()).sort().join(',') === '200,409',
      'Concurrent master edit has exactly one winner');
    product = await get(admin, '/api/v1/products/' + product.productId);
    const oldProduct = product;
    product = await put(
      'products/' + product.productId,
      productBody(product, {
        name: key + ' edited'
      }),
      200,
      'Product edit'
    );
    check(product.version === oldProduct.version + 1, 'Product version increment');
    await put(
      'products/' + product.productId,
      productBody(oldProduct),
      409,
      'Stale master version'
    );
    line = await put(
      'lines/' + line.lineId,
      lineBody(line, {
        name: key + ' edited'
      }),
      200,
      'Line edit'
    );
    machine = await put(
      'equipment/' + machine.equipmentId,
      machineBody(machine, {
        name: key + ' edited'
      }),
      200,
      'Equipment edit'
    );
    user = await put(
      'users/' + user.userId,
      userBody(user, {
        email: 'fixture@example.test'
      }),
      200,
      'User edit'
    );
    const temporary = await client();
    await login(
      temporary,
      key,
      password
    );
    const now = new Date();
    const input = {
      productId: product.productId,
      equipmentId: machine.equipmentId,
      assigneeId: user.userId,
      operationLabel: key,
      targetQty: 10,
      countMode: 'MANUAL',
      counterMappingId: null,
      plannedStart: new Date(now.getTime() - 60000).toISOString(),
      plannedEnd: new Date(now.getTime() + 3600000).toISOString(),
      checklist: [{
        seq: 1,
        label: 'Original',
        required: true
      }]
    };
    order = await post(
      'orders',
      input,
      201,
      'Draft create'
    );
    orders.push(order.orderId);
    await put(
      'products/' + product.productId,
      productBody(product, {
        active: false
      }),
      409,
      'Open-order product deactivate blocked'
    );
    await put(
      'equipment/' + machine.equipmentId,
      machineBody(machine, {
        active: false
      }),
      409,
      'Open-order equipment deactivate blocked'
    );
    await put(
      'lines/' + line.lineId,
      lineBody(line, {
        active: false
      }),
      409,
      'Active line deactivate blocked'
    );
    await put(
      'users/' + user.userId,
      userBody(user, {
        roles: ['ADMIN']
      }),
      409,
      'Assigned-worker role mismatch blocked'
    );
    await put(
      'users/' + user.userId,
      userBody(user, {
        active: false
      }),
      409,
      'Assigned-worker deactivate blocked'
    );
    await put(
      'products/' + product.productId,
      productBody(product, {
        code: key + 'x'
      }),
      409,
      'Product identity frozen after reference'
    );
    await put(
      'equipment/' + machine.equipmentId,
      machineBody(machine, {
        code: key + 'x'
      }),
      409,
      'Equipment identity frozen after reference'
    );
    const draft = {
      ...input,
      expectedVersion: order.version,
      targetQty: 12,
      operationLabel: key + ' changed',
      checklist: [{
        seq: 1,
        label: 'Replacement',
        required: true
      }]
    };
    await read(
      await send(
        worker,
        'PUT',
        '/api/v1/orders/' + order.orderId + '/draft',
        draft
      ),
      403,
      'Worker draft edit denied'
    );
    await put(
      'orders/' + order.orderId + '/draft',
      {
        ...draft,
        checklist: [draft.checklist[0], draft.checklist[0]]
      },
      422,
      'Duplicate checklist sequence rejected'
    );
    const previous = order;
    order = await put(
      'orders/' + order.orderId + '/draft',
      draft,
      200,
      'Draft update'
    );
    check(order.version === previous.version + 1 && order.targetQty === 12
      && order.checklist.length === 1 && order.checklist[0].label === 'Replacement'
      && order.checklist[0].checkId !== previous.checklist[0].checkId,
    'Atomic replacement and single parent version increment');
    await put(
      'orders/' + order.orderId + '/draft',
      draft,
      409,
      'Stale draft rejected'
    );
    order = await post(
      'orders/' + order.orderId + '/release',
      {
        expectedVersion: order.version
      },
      200,
      'Release edited draft'
    );
    await put(
      'orders/' + order.orderId + '/draft',
      {
        ...draft,
        expectedVersion: order.version
      },
      409,
      'Released draft cannot edit'
    );
    await read(
      await send(
        temporary,
        'POST',
        '/api/v1/orders/' + order.orderId + '/cancel',
        {
          expectedVersion: order.version,
          reason: 'Denied'
        }
      ),
      403,
      'Worker cancel denied'
    );
    await post(
      'orders/' + order.orderId + '/cancel',
      {
        expectedVersion: order.version,
        reason: ''
      },
      422,
      'Cancel reason required'
    );
    const stale = order.version;
    order = await post(
      'orders/' + order.orderId + '/cancel',
      {
        expectedVersion: order.version,
        reason: 'Verification cancelled'
      },
      200,
      'Released cancellation'
    );
    check(order.status === 'CANCELLED' && order.version === stale + 1, 'Cancelled status/version');
    await post(
      'orders/' + order.orderId + '/cancel',
      {
        expectedVersion: stale,
        reason: 'Unknown response retry'
      },
      409,
      'Cancellation replay not blind'
    );
    await read(
      await send(
        temporary,
        'POST',
        '/api/v1/orders/' + order.orderId + '/start',
        {
          expectedVersion: order.version
        }
      ),
      409,
      'Cancelled work cannot start'
    );
    const second = await post(
      'orders',
      input,
      201,
      'Second draft'
    );
    orders.push(second.orderId);
    await post(
      'orders/' + second.orderId + '/cancel',
      {
        expectedVersion: second.version,
        reason: 'Draft cancellation'
      },
      200,
      'Draft cancellation'
    );
    const profile = await get(worker, '/api/v1/auth/me');
    let started = await post(
      'orders',
      {
        ...input,
        assigneeId: profile.userId,
        checklist: [],
        targetQty: 1
      },
      201,
      'Started cancellation fixture'
    );
    orders.push(started.orderId);
    started = await post(
      'orders/' + started.orderId + '/release',
      {
        expectedVersion: started.version
      },
      200,
      'Started fixture release'
    );
    started = await read(
      await send(
        worker,
        'POST',
        '/api/v1/orders/' + started.orderId + '/start',
        {
          expectedVersion: started.version
        }
      ),
      200,
      'Started fixture start'
    );
    await post(
      'orders/' + started.orderId + '/cancel',
      {
        expectedVersion: started.version,
        reason: 'Must reject'
      },
      409,
      'Started order cancellation blocked'
    );
    const server = await worker.get('/api/v1/auth/csrf');
    const occurredAt = server.headers()['x-mes-time'].replace(/(\.\d{6})\d+Z$/, '$1Z');
    const ledger = await read(
      await send(
        worker,
        'POST',
        '/api/v1/orders/' + started.orderId + '/entries',
        {
          expectedVersion: started.version,
          entries: [{
            clientEntryId: crypto.randomUUID(),
            kind: 'TOTAL',
            qty: 1,
            reversalOf: null,
            correctionId: null,
            reason: null, occurredAt
          }]
        }
      ),
      200,
      'Started fixture real manual entry'
    );
    started = await read(
      await send(
        worker,
        'POST',
        '/api/v1/orders/' + started.orderId + '/submit',
        {
          expectedVersion: ledger.version,
          completionReason: null
        }
      ),
      200,
      'Started fixture submit'
    );
    await post(
      'orders/' + started.orderId + '/cancel',
      {
        expectedVersion: started.version,
        reason: 'Must reject'
      },
      409,
      'Submitted cancellation blocked'
    );
    started = await post(
      'orders/' + started.orderId + '/close',
      {
        expectedVersion: started.version,
        reason: 'Verification complete'
      },
      200,
      'Started fixture close'
    );
    await post(
      'orders/' + started.orderId + '/cancel',
      {
        expectedVersion: started.version,
        reason: 'Must reject'
      },
      409,
      'Closed cancellation blocked'
    );
    const hidden = await get(worker, '/api/v1/equipment?limit=100');
    check(
      hidden.items.some(x => x.equipmentId === machine.equipmentId),
      'Worker own-reference master scope'
    );
    product = await put(
      'products/' + product.productId,
      productBody(product, {
        active: false
      }),
      200,
      'Product soft deactivate after cancel'
    );
    machine = await put(
      'equipment/' + machine.equipmentId,
      machineBody(machine, {
        active: false
      }),
      200,
      'Equipment soft deactivate after cancel'
    );
    line = await put(
      'lines/' + line.lineId,
      lineBody(line, {
        active: false
      }),
      200,
      'Line soft deactivate after equipment'
    );
    user = await put(
      'users/' + user.userId,
      userBody(user, {
        roles: ['ADMIN']
      }),
      200,
      'Role change after cancellation'
    );
    await read(
      await temporary.get('/api/v1/auth/me'),
      401,
      'Role change invalidates cookie'
    );
    user = await put(
      'users/' + user.userId,
      userBody(user, {
        active: false
      }),
      200,
      'User soft deactivate'
    );
    check(!(await get(admin, '/api/v1/products/' + product.productId)).active,
      'Soft-deactivated row preserved');
  } finally {
    // Reconcile and cancel only this run’s own unfinished fixtures. Never delete audit/history.
    for (const id of orders) {
      try {
        const row = await get(admin, '/api/v1/orders/' + id);
        if (['DRAFT', 'RELEASED'].includes(row.status)) {
          await send(
            admin,
            'POST',
            '/api/v1/orders/' + id + '/cancel',
            {
              expectedVersion: row.version,
              reason: 'Verification fixture reconciliation'
            }
          );
        }
      } catch { /* Failure remains visible in the process exit; preserve history. */ }
    }
    fs.writeFileSync(path.join(root, 'verification-results/master-api.json'),
      JSON.stringify(
        {
          checks,
          count: checks.length,
          fixture: key,
          orders
        },
        null,
        2
      ));
    for (const c of clients) await c.dispose();
  }
})().catch(error => { console.error(error.message); process.exitCode = 1; });
