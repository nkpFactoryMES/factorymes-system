// 알림 설정 화면(/notification-settings)과 설정 API 권한을 실제 브라우저로 확인한다.
// 규격: AI협업폴더 FactoryMES-수신자채널-업무라우팅-동시구현분담-v1.md.
//   작업자(portal): 메뉴에 "알림 설정", 본인 경보 4종 읽기 전용, 관리 영역 없음,
//                   관리 API(사용자 목록·변경) 403
//   관리자(admin): 본인 6종 표, 사용자 목록 → portal 설정 팝업(경보 4종만, AI 없음 안내)
//     → 사유 없이 저장하면 보내기 전 거절 → 사유와 함께 ALARM_OPENED 이메일 LOCAL·채팅 MOCK 저장
//       → API 값·버전 증가 확인 → 작업자 화면에도 반영
//     → 다른 곳에서 먼저 바뀐 뒤 저장하면 409: 입력 보존·충돌 표시·최신 저장값 재조회 → 다시 저장 성공
//     → WORKER에게 AI 설정 422, 낡은 버전 409(API)
//   → 390px 넘침 없음, 캡처. 끝나면 portal ALARM_OPENED를 시작 값으로 되돌린다(이력은 남는다).
// 주소·URL·키 입력이 없는지도 본다. 경보·AI 발생→실제 발송 대조는 Tooling 통합 검증이 맡는다.
// 실행: Backend·Web(Contracts 1.1.0)과 05_notification_settings_upgrade.sql 적용 뒤
// `node verify-notification-settings.cjs`.
const { chromium } = require('playwright');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';

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

async function login(browser, user, secret, viewport) {
  const context = await browser.newContext({
    ignoreHTTPSErrors: true,
    viewport
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
  const secrets = process.env.MES_SECRET_DIRECTORY || path.join(project, '.secrets');
  await page.locator('#password').fill(
    fs.readFileSync(path.join(secrets, secret), 'utf8').trim()
  );
  await button(page, '로그인').click();
  await page.locator('#workspace').waitFor({ state: 'visible' });
  return page;
}

// 로그인한 브라우저의 쿠키와 CSRF로 API를 호출한다(확인·권한 검사·정리용).
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
    throw new Error(`${label}: ${method} ${url} HTTP ${result.status} `
      + JSON.stringify(result.json));
  }
  return result.json;
}

async function noPageOverflow(page, label) {
  const extra = await page.evaluate(
    () => document.documentElement.scrollWidth - document.documentElement.clientWidth
  );
  if (extra > 1) {
    throw new Error(`${label}: page is ${extra}px wider than viewport`);
  }
}

function settingOf(view, kind) {
  const found = view.settings.find(x => x.eventKind === kind);
  if (!found) {
    throw new Error('Setting row missing: ' + kind);
  }
  return found;
}

const alarmKinds = ['ALARM_OPENED', 'ALARM_RECOVERED', 'ALARM_CLOSED', 'ALARM_FORCE_CLOSED'];
const allKinds = [...alarmKinds, 'AI_COMPLETED', 'AI_SCHEDULED'];

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });
  const desktop = {
    width: 1360,
    height: 1000
  };

  let admin = null;
  let portalId = null;
  let original = null;

  try {
    admin = await login(browser, 'admin', 'admin_password', desktop);
    const worker = await login(browser, 'portal', 'portal_password', desktop);
    portalId = (await expectStatus(worker, 'GET', '/api/v1/auth/me', undefined, 200, 'Me'))
      .userId;
    original = settingOf(
      await expectStatus(
        admin,
        'GET',
        '/api/v1/notification-settings/users/' + portalId,
        undefined,
        200,
        'Portal settings'
      ),
      'ALARM_OPENED'
    );

    // 1. 작업자: 메뉴·본인 경보 4종 읽기 전용·관리 영역 없음·관리 API 403
    await worker.goto(base + '/notification-settings');
    await worker.getByRole('heading', {
      name: '알림 설정',
      exact: true
    }).waitFor();
    await status(worker, '완료했습니다.');
    await worker.locator('nav.shell-menu a').filter({ hasText: '알림 설정' }).waitFor();
    const workerRows = await worker.locator('section.my-settings tbody tr').count();
    if (workerRows !== 4) {
      throw new Error('Worker should see 4 alarm rows, saw ' + workerRows);
    }
    if (await worker.locator('section.setting-users').count() !== 0
      || await worker.locator('main select, main input').count() !== 0) {
      throw new Error('Worker page must be read-only without management area');
    }
    const mine = await expectStatus(
      worker,
      'GET',
      '/api/v1/notification-settings/me',
      undefined,
      200,
      'Worker me'
    );
    if (JSON.stringify(mine.settings.map(x => x.eventKind)) !== JSON.stringify(alarmKinds)) {
      throw new Error('Worker settings should be alarm kinds only: ' + JSON.stringify(mine));
    }
    for (const [method, url, body] of [
      ['GET', '/api/v1/notification-settings/users', undefined],
      [
        'PUT',
        `/api/v1/notification-settings/users/${portalId}/ALARM_OPENED`,
        {
          expectedVersion: original.version,
          emailMode: 'LOCAL',
          chatMode: 'LOCAL',
          reason: 'worker must not change'
        }
      ]
    ]) {
      await expectStatus(worker, method, url, body, 403, 'Worker admin API');
    }
    check('Worker: menu, own 4 alarm settings read-only, admin APIs 403');

    // 2. 관리자: 본인 6종, 사용자 목록, portal 팝업(경보 4종만)
    await admin.goto(base + '/notification-settings');
    await status(admin, '완료했습니다.');
    const adminRows = await admin.locator('section.my-settings tbody tr').count();
    if (adminRows !== 6) {
      throw new Error('Admin should see 6 own rows, saw ' + adminRows);
    }
    const userRow = admin.locator('section.setting-users tbody tr')
      .filter({ hasText: '(portal)' });
    await button(userRow, '설정 보기').click();
    const dialog = admin.getByRole('dialog', { name: /^알림 설정 · / });
    await dialog.waitFor();
    if (await dialog.locator('table.setting-edit tbody tr').count() !== 4) {
      throw new Error('Worker target dialog should list 4 alarm kinds');
    }
    await dialog.getByText('AI 분석 설정이 없습니다', { exact: false }).waitFor();
    if (await dialog.locator('input:not([maxlength="300"])').count() !== 0) {
      throw new Error('Dialog must not ask address, URL or key');
    }
    check('Admin: own 6 kinds, user list, worker dialog shows 4 alarm kinds and no address input');

    // 3. 사유 없이 저장 → 보내기 전 거절
    const opened = dialog.locator('table.setting-edit tbody tr')
      .filter({ hasText: 'ALARM_OPENED' });
    let puts = 0;
    const countPut = request => {
      if (request.method() === 'PUT' && request.url().includes('/notification-settings/')) {
        puts++;
      }
    };
    admin.on('request', countPut);
    await dialog.getByLabel('변경 사유').fill('');
    await button(opened, '저장 · 경보 발생(통신 끊김·상한 초과)').click();
    await status(admin, '변경 사유');
    admin.off('request', countPut);
    if (puts !== 0) {
      throw new Error('Save without reason must not send a request');
    }
    check('Save without reason is refused before sending');

    // 4. 사유와 함께 저장: 이메일 LOCAL·채팅 MOCK
    const target = {
      email: original.emailMode === 'LOCAL' ? 'MOCK' : 'LOCAL',
      chat: original.chatMode === 'MOCK' ? 'OFF' : 'MOCK'
    };
    await dialog.getByLabel('변경 사유').fill('브라우저 검증: 발생 알림 채널 변경');
    await opened.getByLabel('이메일 · 경보 발생(통신 끊김·상한 초과)').selectOption(target.email);
    await opened.getByLabel('채팅 · 경보 발생(통신 끊김·상한 초과)').selectOption(target.chat);
    const saved = admin.waitForResponse(response => response.request().method() === 'PUT'
      && response.url().endsWith(`/notification-settings/users/${portalId}/ALARM_OPENED`));
    await button(opened, '저장 · 경보 발생(통신 끊김·상한 초과)').click();
    const savedResponse = await saved;
    const body = savedResponse.request().postDataJSON();
    await status(admin, '설정을 저장했습니다.');
    if (savedResponse.status() !== 200 || body.expectedVersion !== original.version
      || Object.keys(body).sort().join(',') !== 'chatMode,emailMode,expectedVersion,reason') {
      throw new Error('Save request unexpected: ' + savedResponse.status() + JSON.stringify(body));
    }
    const afterSave = settingOf(
      await expectStatus(
        admin,
        'GET',
        '/api/v1/notification-settings/users/' + portalId,
        undefined,
        200,
        'After save'
      ),
      'ALARM_OPENED'
    );
    if (afterSave.emailMode !== target.email || afterSave.chatMode !== target.chat
      || afterSave.version !== original.version + 1) {
      throw new Error('Saved setting differs: ' + JSON.stringify(afterSave));
    }
    await worker.reload();
    await status(worker, '완료했습니다.');
    await worker.locator('section.my-settings tbody tr')
      .filter({ hasText: '경보 발생' })
      .filter({ hasText: `v${afterSave.version}` })
      .waitFor();
    check('Admin saves modes with reason; version +1; worker page shows the new value');

    // 5. 충돌: 다른 곳(API)에서 먼저 바꾼 뒤 화면에서 저장 → 409, 입력 보존, 재조회 후 재저장 성공
    await expectStatus(
      admin,
      'PUT',
      `/api/v1/notification-settings/users/${portalId}/ALARM_OPENED`,
      {
        expectedVersion: afterSave.version,
        emailMode: 'OFF',
        chatMode: 'OFF',
        reason: '동시 변경 재현'
      },
      200,
      'Concurrent change'
    );
    await opened.getByLabel('채팅 · 경보 발생(통신 끊김·상한 초과)').selectOption('LOCAL');
    await button(opened, '저장 · 경보 발생(통신 끊김·상한 초과)').click();
    await status(admin, '다른 변경과 충돌했습니다');
    await opened.locator('.badge').filter({ hasText: '다른 변경 있음' }).waitFor();
    const keptChat = await opened.getByLabel('채팅 · 경보 발생(통신 끊김·상한 초과)')
      .inputValue();
    const latestShown = (await opened.textContent()).includes(`v${afterSave.version + 1}`);
    if (keptChat !== 'LOCAL' || !latestShown) {
      throw new Error('Conflict must keep input and show the latest saved version');
    }
    await button(opened, '저장 · 경보 발생(통신 끊김·상한 초과)').click();
    await status(admin, '설정을 저장했습니다.');
    const afterRetry = settingOf(
      await expectStatus(
        admin,
        'GET',
        '/api/v1/notification-settings/users/' + portalId,
        undefined,
        200,
        'After retry'
      ),
      'ALARM_OPENED'
    );
    if (afterRetry.chatMode !== 'LOCAL' || afterRetry.version !== afterSave.version + 2) {
      throw new Error('Retry after conflict should save on the latest version');
    }
    check('Concurrent change → 409 keeps input, shows latest version; explicit re-save succeeds');

    // 6. API 규칙: WORKER AI 설정 422, 낡은 버전 409
    await expectStatus(
      admin,
      'PUT',
      `/api/v1/notification-settings/users/${portalId}/AI_COMPLETED`,
      {
        expectedVersion: 0,
        emailMode: 'LOCAL',
        chatMode: 'OFF',
        reason: 'worker cannot receive AI'
      },
      422,
      'Worker AI setting'
    );
    await expectStatus(
      admin,
      'PUT',
      `/api/v1/notification-settings/users/${portalId}/ALARM_OPENED`,
      {
        expectedVersion: afterRetry.version - 1,
        emailMode: 'OFF',
        chatMode: 'OFF',
        reason: 'stale version'
      },
      409,
      'Stale version'
    );
    check('API: AI setting for WORKER 422, stale version 409');

    await admin.screenshot({
      path: path.join(output, 'FactoryMES-notification-settings-screen.png'),
      fullPage: true
    });
    await button(dialog, '닫기').click();
    await dialog.waitFor({ state: 'detached' });

    // 7. 390px: 본문 넘침 없음
    const phone = await login(browser, 'admin', 'admin_password', {
      width: 390,
      height: 844
    });
    await phone.goto(base + '/notification-settings');
    await status(phone, '완료했습니다.');
    await noPageOverflow(phone, 'Phone notification settings');
    check('Phone width 390: no page overflow');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-notification-settings-verification.json'),
      JSON.stringify(
        {
          passed: checks.length,
          checks,
          runtimeErrors: errors,
          eventKinds: allKinds,
          externalDeliveryCalls: 0,
          notCovered: 'Alarm/AI event → routed delivery is verified by Tooling integration.',
          note: 'portal ALARM_OPENED restored to its starting modes; audit history retained.'
        },
        null,
        2
      )
    );
    console.log('Browser notification settings verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const text = await page.locator('main > p[role="status"]')
          .textContent({ timeout: 1000 })
          .catch(() => 'none');
        console.error('PAGE STATUS: ' + text);
      }
    }
    process.exitCode = 1;
  } finally {
    // 정리: portal ALARM_OPENED를 시작 모드로 되돌린다(버전·감사 이력은 남는다).
    if (admin && portalId && original) {
      try {
        const current = settingOf(
          (await api(admin, 'GET', '/api/v1/notification-settings/users/' + portalId)).json,
          'ALARM_OPENED'
        );
        if (current.emailMode !== original.emailMode || current.chatMode !== original.chatMode) {
          const restored = await api(
            admin,
            'PUT',
            `/api/v1/notification-settings/users/${portalId}/ALARM_OPENED`,
            {
              expectedVersion: current.version,
              emailMode: original.emailMode,
              chatMode: original.chatMode,
              reason: '브라우저 검증 종료: 시작 값으로 되돌림'
            }
          );
          if (restored.status !== 200) {
            console.error('CLEANUP: restore HTTP ' + restored.status);
            process.exitCode = 1;
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
