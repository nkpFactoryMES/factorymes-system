// 기준정보(품목·라인·설비·사용자) 관리와 작업지시 초안 수정·취소를 실제 브라우저로 확인한다.
// 실행마다 고유 코드로 테스트 데이터를 만들고, 기존 업무 자료는 건드리지 않는다.
// 실행: GPT가 Backend·부모 연결과 컨테이너 배포를 마친 뒤 `node verify-master-draft.cjs`.
const { chromium } = require('playwright');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

const project = path.resolve(__dirname, '../..');
const output = path.join(project, 'verification-results');
const base = 'https://localhost:7443';
const adminLogin = process.env.MES_BROWSER_ADMIN_LOGIN ?? 'admin';
const adminSecret = process.env.MES_BROWSER_ADMIN_SECRET ?? 'admin_password';
const workerLogin = process.env.MES_BROWSER_WORKER_LOGIN ?? 'portal';
const workerSecret = process.env.MES_BROWSER_WORKER_SECRET ?? 'portal_password';

const key = crypto.randomUUID().slice(0, 8).toUpperCase();
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

// 작업 상세 안 탭(기본 / 실적·제출 / 중지·정정 / 초안·취소)을 고른다. 숨긴 탭의 입력은 조작할 수 없다.
async function detailTab(page, name) {
  await page.getByRole('tablist', { name: '작업 상세' })
    .getByRole('tab', {
      name,
      exact: true
    })
    .click();
}

function row(page, text) {
  return page.getByRole('row').filter({ hasText: text });
}

// 화면 상단 상태 문구(첫 번째 role=status)가 "완료했습니다."가 될 때까지 기다린다.
async function done(page) {
  await page.waitForFunction(() =>
    document.querySelector('main > p[role="status"]')?.textContent === '완료했습니다.'
    && !document.querySelector('main > fieldset')?.disabled
  );
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

// 기준정보 화면은 종류별 탭이다. 탭을 고르고 상단 "새 X 등록"으로 등록 팝업을 연다.
async function showTab(page, name) {
  await page.getByRole('tab', {
    name,
    exact: true
  }).click();
}

async function openCreate(page, name) {
  await showTab(page, name);
  await button(page.locator('main .toolbar'), '새 ' + name + ' 등록').click();
  await page.getByRole('dialog', { name: name + ' 등록' }).waitFor();
}

// 표의 첫 행이 방금 저장한 코드인지 본다(등록 직후 사용자가 바로 확인할 수 있는 자리).
async function expectTopRow(page, section, code) {
  const first = await page.locator(section + ' tbody tr').first().textContent();
  if (!first.includes(code)) {
    throw new Error(`${section}: first row is not the new ${code}: ${first.trim()}`);
  }
}

// 행이 아직 목록에 없으면 "더 보기"를 눌러 다음 페이지를 읽는다(다음 페이지가 없으면 실패).
async function findRow(page, section, moreName, code) {
  while (await page.locator(section + ' tbody tr').filter({ hasText: code }).count() === 0) {
    const more = button(page.locator(section), moreName);
    if (await more.count() === 0) {
      throw new Error(`${section}: ${code} not found after reading every page`);
    }
    await more.click();
    await done(page);
  }
}

async function openMasterData(page) {
  await page.goto(base + '/master-data');
  await page.getByRole('heading', {
    name: '기준정보',
    exact: true
  }).waitFor();
}

(async () => {
  fs.mkdirSync(output, { recursive: true });
  const browser = await chromium.launch({
    ...(process.env.MES_BROWSER_EXECUTABLE
      ? { executablePath: process.env.MES_BROWSER_EXECUTABLE } : {}),
    headless: true
  });

  const lineCode = 'L-' + key;
  const lineName = '검증 라인 ' + key;
  const productCode = 'P-' + key;
  const productName = '검증 품목 ' + key;
  const renamedProduct = '검증 품목(수정) ' + key;
  const equipmentCode = 'E-' + key;
  const equipmentName = '검증 설비 ' + key;
  const spareCode = 'E2-' + key;
  const userLogin = 'verify.' + key.toLowerCase();
  const userName = '검증 작업자 ' + key;
  const title = '초안 검증 ' + key;
  const editedTitle = '초안 검증(수정) ' + key;

  try {
    // 1. 관리자: 기준정보 등록
    const admin = await login(browser, adminLogin, adminSecret);
    await openMasterData(admin);
    await done(admin);

    await openCreate(admin, '라인');
    await admin.getByLabel('라인 코드').fill(lineCode);
    await admin.getByLabel('라인 이름').fill(lineName);
    await button(admin, '라인 저장').click();
    await row(admin, lineCode).waitFor();
    await expectTopRow(admin, 'section.master-lines', lineCode);
    check('Admin creates line');

    await openCreate(admin, '품목');
    await admin.getByLabel('품목 코드').fill(productCode);
    await admin.getByLabel('품목 이름').fill(productName);
    await button(admin, '품목 저장').click();
    await row(admin, productCode).waitFor();
    await expectTopRow(admin, 'section.master-products', productCode);
    check('Admin creates product (unit EA)');

    await openCreate(admin, '설비');
    await admin.getByLabel('설비 코드').fill(equipmentCode);
    await admin.getByLabel('설비 이름').fill(equipmentName);
    await admin.getByLabel('소속 라인').selectOption({ label: `${lineName} (${lineCode})` });
    await button(admin, '설비 저장').click();
    await row(admin, equipmentCode).waitFor();
    // 설비가 한 페이지(50건)를 넘어도, 방금 등록한 설비는 목록 맨 위에 바로 보인다.
    await expectTopRow(admin, 'section.master-equipment', equipmentCode);
    // "설비 더 보기"로 다음 페이지를 읽어도 같은 설비가 두 번 나오지 않는다(커서 유지).
    const moreEquipment = button(admin.locator('section.master-equipment'), '설비 더 보기');
    if (await moreEquipment.count()) {
      const before = await admin.locator('section.master-equipment tbody tr').count();
      await moreEquipment.click();
      await done(admin);
      const after = await admin.locator('section.master-equipment tbody tr').count();
      if (after <= before) {
        throw new Error(`More equipment did not append rows: ${before} -> ${after}`);
      }
    }
    if (await row(admin, equipmentCode).count() !== 1) {
      throw new Error('New equipment row must appear exactly once');
    }
    check('Admin creates equipment on the new line; new row shown first, no duplicate on more');

    // 비밀번호는 실행마다 새로 만들고 출력하지 않는다.
    await openCreate(admin, '사용자');
    await admin.getByLabel('로그인 ID').fill(userLogin);
    await admin.getByLabel('사용자 이름').fill(userName);
    await admin.getByLabel('역할').selectOption('WORKER');
    await admin.getByLabel('초기 비밀번호 (12자 이상)').fill(crypto.randomBytes(18).toString('base64'));
    await button(admin.getByRole('dialog', { name: '사용자 등록' }), '사용자 등록').click();
    await row(admin, userLogin).waitFor();
    await expectTopRow(admin, 'section.master-users', userLogin);
    // 저장하면 팝업이 닫힌다. 다시 열었을 때 비밀번호 칸이 비어 있어야 한다.
    await openCreate(admin, '사용자');
    if (await admin.getByLabel('초기 비밀번호 (12자 이상)').inputValue() !== '') {
      throw new Error('Password field must be cleared after save');
    }
    await button(admin.getByRole('dialog', { name: '사용자 등록' }), '닫기').click();
    check('Admin creates worker user; password field cleared');

    // 2. 관리자: 현재 값을 불러와 수정, 사용 중지
    await showTab(admin, '품목');
    await button(row(admin, productCode), '품목 수정').click();
    if (await admin.getByLabel('품목 이름').inputValue() !== productName) {
      throw new Error('Edit did not load current product values');
    }
    await admin.getByLabel('품목 이름').fill(renamedProduct);
    await button(admin, '품목 저장').click();
    await row(admin, renamedProduct).waitFor();
    check('Admin edits product name from current values');

    await openCreate(admin, '설비');
    await admin.getByLabel('설비 코드').fill(spareCode);
    await admin.getByLabel('설비 이름').fill('검증 예비 설비 ' + key);
    await admin.getByLabel('소속 라인').selectOption({ label: `${lineName} (${lineCode})` });
    await button(admin, '설비 저장').click();
    await row(admin, spareCode).waitFor();
    await expectTopRow(admin, 'section.master-equipment', spareCode);
    await button(row(admin, spareCode), '설비 수정').click();
    await admin.getByLabel('설비 사용').uncheck();
    await button(admin, '설비 저장').click();
    await row(admin, spareCode).filter({ hasText: '중지' }).waitFor();
    check('Admin deactivates unused equipment via edit');

    // 3. 작업자: 기준정보 관리 권한 없음(화면과 API 모두)
    const worker = await login(browser, workerLogin, workerSecret);
    await openMasterData(worker);
    await worker.getByText('기준정보 관리는 관리자만 사용할 수 있습니다.').waitFor();
    if (await button(worker, '품목 저장').count()) {
      throw new Error('Worker must not see master data forms');
    }
    const workerStatus = await worker.evaluate(async () => {
      const csrf = await (await fetch('/api/v1/auth/csrf', { cache: 'no-store' })).json();
      const response = await fetch('/api/v1/products', {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          'X-CSRF-TOKEN': csrf.requestToken
        },
        body: JSON.stringify({
          code: 'X-WORKER',
          name: 'worker attempt',
          unit: 'EA',
          active: true
        })
      });
      return response.status;
    });
    if (workerStatus !== 403) {
      throw new Error('Worker product create returned ' + workerStatus);
    }
    check('Worker has no master data UI and API create is 403');

    // 4. 관리자: 새 기준정보로 초안 작성 → 초안 수정
    await admin.goto(base + '/orders');
    await button(admin, '새로고침').waitFor();
    await done(admin);
    // 작업지시 작성은 상단 버튼으로 여는 팝업이다(목록 아래 상시 폼 없음).
    await admin.getByRole('button', {
      name: '작업지시 작성',
      exact: true
    }).click();
    await admin.locator('section.order-create').waitFor();
    await admin.getByLabel('품목', { exact: false })
      .first()
      .selectOption({ label: `${renamedProduct} (${productCode})` });
    await admin.getByLabel('설비', { exact: false })
      .first()
      .selectOption({ label: equipmentName });
    await admin.getByLabel('담당 작업자')
      .first()
      .selectOption({ label: `${userName} (${userLogin})` });
    await admin.getByLabel('작업명', { exact: true }).fill(title);
    await admin.getByLabel('목표 수량 (EA)').first().fill('10');
    await button(admin, '초안 저장').click();
    await admin.getByRole('heading', { name: '작업 상세 · ' + title }).waitFor();
    check('Admin creates draft with new master data');

    const editor = admin.locator('section.draft-editor');
    await detailTab(admin, '초안·취소');
    await button(editor, '초안 수정 열기').click();
    await editor.getByLabel('초안 목표 수량 (EA)').waitFor();
    if (await editor.getByLabel('초안 목표 수량 (EA)').inputValue() !== '10') {
      throw new Error('Draft editor did not load current values');
    }
    await editor.getByLabel('초안 작업명').fill(editedTitle);
    await editor.getByLabel('초안 목표 수량 (EA)').fill('25');
    await editor.getByLabel('초안 점검 항목', { exact: false }).fill('설비 외관 점검\n선택:주변 정리');
    await button(editor, '초안 변경 저장').click();
    await admin.getByRole('heading', { name: '작업 상세 · ' + editedTitle }).waitFor();
    const detail = admin.locator('section')
      .filter({ has: admin.getByRole('heading', { name: '작업 상세 · ' + editedTitle }) })
      .first();
    await detailTab(admin, '기본');
    await detail.getByText('목표 25', { exact: false }).waitFor();
    await detail.getByText('주변 정리', { exact: false }).waitFor();
    check('Admin edits draft (label, quantity, checklist incl. optional item)');

    // 5. 발행 후에는 초안 수정 불가, 취소는 가능
    await button(admin, '작업지시 발행').click();
    await detail.getByText('RELEASED', { exact: false }).waitFor();
    // 숨긴 탭의 버튼은 세지 않으므로, 초안·취소 탭을 연 상태에서 없는지 확인한다.
    await detailTab(admin, '초안·취소');
    if (await button(admin, '초안 수정 열기').count() || await button(admin, '초안 변경 저장').count()) {
      throw new Error('RELEASED order must not offer draft editing');
    }
    check('RELEASED order has no draft edit controls');

    await editor.getByLabel('취소 사유').fill('검증용 작업지시 취소');
    await button(editor, '작업지시 취소').click();
    await detail.getByText('CANCELLED', { exact: false }).waitFor();
    if (await button(admin, '작업지시 취소').count()) {
      throw new Error('CANCELLED order must not offer cancel again');
    }
    check('Admin cancels RELEASED (not started) order; CANCELLED shown');

    // 6. 정리: 만든 테스트 사용자를 비활성화(삭제는 하지 않는다)
    await openMasterData(admin);
    await done(admin);
    await showTab(admin, '사용자');
    // 새로 연 화면은 첫 페이지만 읽는다. 사용자가 하듯 "사용자 더 보기"로 찾아 들어간다.
    await findRow(admin, 'section.master-users', '사용자 더 보기', userLogin);
    await button(row(admin, userLogin), '사용자 수정').click();
    await admin.getByLabel('사용자 사용').uncheck();
    await button(admin, '사용자 저장').click();
    await row(admin, userLogin).filter({ hasText: '중지' }).waitFor();
    check('Admin deactivates the test user via edit');

    if (errors.length) {
      throw new Error('Browser runtime errors: ' + errors.join('; '));
    }

    const report = {
      passed: checks.length,
      checks,
      runtimeErrors: errors,
      testKey: key,
      cloudApiCalls: 0,
      externalDeliveryCalls: 0,
      note: 'Test line/product/equipment remain with unique codes; test user deactivated; '
        + 'one CANCELLED order retained. Secrets and cookies not saved.'
    };
    fs.writeFileSync(
      path.join(output, 'FactoryMES-browser-master-draft-verification.json'),
      JSON.stringify(
        report,
        null,
        2
      )
    );
    console.log('Browser master data and draft verified');
  } catch (error) {
    console.error(error.message);
    for (const context of contexts) {
      const page = context.pages()[0];
      if (page) {
        const status = await page.locator('main > p[role="status"]')
          .textContent()
          .catch(() => 'none');
        const editor = await page.locator('section.draft-editor [role="status"]')
          .textContent()
          .catch(() => 'none');
        console.error(`PAGE STATUS: ${status} | EDITOR STATUS: ${editor}`);
      }
    }
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
