import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { pathToFileURL } from "node:url";

const { chromium, firefox } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : "playwright");
const address = process.argv[2];
const pluginDriver = process.env.QUASAR_TEST_PLUGIN_DRIVER
    ? (await import(pathToFileURL(process.env.QUASAR_TEST_PLUGIN_DRIVER).href)).checkPlugin : undefined;
if (process.env.QUASAR_TEST_PLUGIN_DRIVER) assert.equal(typeof pluginDriver, "function", "Browser driver must export checkPlugin(page, base)");

function observeDashboard(context, circuits, errors) {
    context.on("response", response => {
        if (response.status() >= 500 && !new URL(response.url()).pathname.endsWith("/rtc/attempts"))
            errors.push(response.status() + " " + response.url());
    });
    const record = event => {
        if (/(?:^|\/)_blazor(?:\/|$)/.test(new URL(event.url()).pathname)) circuits.push(event.url());
    };
    context.on("request", record);
    context.on("page", page => page.on("websocket", record));
}

for (const browserType of [chromium, firefox]) {
    const browser = await browserType.launch(browserType === chromium ? { executablePath: process.env.CHROME_PATH } : {});
    try {
        const context = await browser.newContext();
        const errors = [], circuits = [], shared = [], fallbacks = [];
        observeDashboard(context, circuits, errors);
        const page = await context.newPage();
        page.on("response", r => {
            if (r.url().includes("/_nebula/releases/")) shared.push([r.url(), r.status()]);
            if (/\/fixture-panel\/_framework\/Quasar\.Client\..+\.wasm$/.test(r.url())) fallbacks.push(r.status());
        });
        page.on("pageerror", error => { errors.push(error.message); console.error(error); });
        page.on("console", msg => { if (msg.type() === "error") console.error(msg.text()); });
        for (const prefix of ["/", "/fixture-panel/"]) {
            const base = address + prefix;
            if (process.env.QUASAR_FLEET_CHECKS === "1") await (await import("./fleet.test.mjs")).checkFleet(browser, base);
            if (prefix === "/") assert.equal((await context.request.get(base + "settings/display")).status(), 401);
            await context.request.get(base + "fixture/login");
            await page.goto(base + "fixture/tool/42");
            const heading = page.getByRole("heading", { name: "Fixture tool 42" });
            await heading.waitFor();
            const framework = await page.evaluate(() => {
                const state = JSON.parse(document.querySelector("#quasar-connection").textContent);
                return Object.keys(state.frameworkIntegrity || {}).find(name => /^Quasar\.Client\..+\.wasm$/.test(name));
            });
            if (prefix !== "/") assert.ok(framework, "Hosted release should advertise its fingerprinted client assembly");
            if (framework) {
                const url = base + "_framework/" + framework;
                const first = await context.request.get(url, {headers: {"Accept-Encoding": "identity"}});
                assert.equal(first.status(), 200);
                const etag = first.headers().etag;
                assert.equal(etag, '"' + createHash("sha256").update(await first.body()).digest("base64") + '"');
                assert.equal((await context.request.get(url, {headers: {"Accept-Encoding": "identity", "If-None-Match": etag}})).status(), 304);
                const anonymous = await browser.newContext();
                try { assert.equal((await anonymous.request.get(url, {headers: {"If-None-Match": etag}})).status(), 401); }
                finally { await anonymous.close(); }
                console.log("Verified authorized identity 304 and anonymous framework denial at " + prefix);
            }
            await page.getByRole("button", {name: "Count 0"}).click();
            await page.getByRole("button", {name: "Count 1"}).waitFor();
            await page.getByRole("link", {name: "Details tab"}).click();
            await page.waitForURL("**/fixture/tool/42?tab=details");
            await page.getByRole("button", {name: "Count 1"}).waitFor();
            await page.reload();
            await page.getByRole("button", {name: "Count 0"}).waitFor();
            await page.getByRole("link", {name: "Restricted plugin tool"}).click();
            await page.getByText("You do not have permission to view this page.").waitFor();
            await page.goBack();
            await heading.waitFor();
            await page.getByRole("link", {name: "Worker plugin tool"}).click();
            await page.waitForURL(base + "fixture/worker-tool");
            await page.getByText(/page not found|^not found$/i).first().waitFor();
            assert.equal(await page.getByRole("heading", {name: "Fixture worker plugin"}).count(), 0);
            for (const route of ["fixture/unknown", "fixture/worker-tool"]) {
                const missing = await page.goto(base + route);
                assert.equal(missing.status(), 404, route);
                await page.getByRole("heading", {name: "Not Found"}).waitFor();
                assert.doesNotMatch(await missing.text(), /Fixture worker plugin|Worker count/);
            }
            console.log(browserType.name() + " rejected the unpublished worker page at " + prefix);
            if (pluginDriver) {
                const operator = await browser.newContext();
                observeDashboard(operator, circuits, errors);
                try {
                    const login = await operator.request.get(base + "fixture/plugin-login");
                    assert.equal(login.status(), 200);
                    const pluginPage = await operator.newPage();
                    pluginPage.on("pageerror", error => errors.push(error.message));
                    await pluginDriver(pluginPage, base);
                } finally { await operator.close(); }
            }
            const profileQuery = prefix === "/" ? "profileId" : "profile";
            await page.goto(base + "configs?" + profileQuery + "=survival");
            await page.getByRole("heading", {name: "Survival test", exact: true}).waitFor();
            await page.getByText("Access lists", {exact: true}).click();
            await page.getByText("76561198000000003", {exact: true}).waitFor();
            assert.ok(!(await page.content()).includes("fixture-hidden-password"));
            if (process.env.QUASAR_VIEWER_PACKAGE) await (await import("./viewer.test.mjs")).checkViewer(page, base);
            const response = await page.goto(base + "settings/display");
            assert.equal(response.status(), 200);
            await page.locator("#quasar-wasm-loading[hidden]").waitFor({ state: "attached", timeout: 30000 });
            const input = page.getByRole("combobox", { name: "Time zone" });
            await input.fill("Asia/Kolkata");
            await page.getByRole("button", { name: "Save", exact: true }).click();
            await page.getByText("Time zone saved.").waitFor();
            assert.equal(await page.evaluate(() => quasarConfigs.getLocalStorage("quasar.timeZone")), "Asia/Kolkata");
            if (prefix === "/") {
                await page.emulateMedia({ colorScheme: "light" });
                await page.evaluate(() => quasarConfigs.setLocalStorage("quasar.theme.mode", '"dark"'));
                await page.reload();
                const shell = page.locator("#quasar-wasm-shell");
                const theme = page.getByRole("combobox", {name: "Color theme"});
                await page.waitForFunction(() => document.getElementById("quasar-wasm-theme").value === "dark");
                assert.equal(await shell.getAttribute("data-theme"), "dark");
                await theme.selectOption("system");
                assert.equal(await shell.getAttribute("data-theme"), "light");
                await page.emulateMedia({ colorScheme: "dark" });
                await page.waitForFunction(() => document.getElementById("quasar-wasm-shell").dataset.theme === "dark");
            }
            // A routed link must keep the runtime and the selected SDK transport alive.
            const generation = await page.evaluate(() => { window.fixtureGeneration = crypto.randomUUID(); return window.fixtureGeneration; });
            await page.evaluate(() => {
                const link = document.createElement("a");
                link.href = new URL("hosts/enroll", document.baseURI);
                link.textContent = "Fixture restricted route";
                document.getElementById("quasar-main").append(link);
            });
            await page.getByRole("link", {name: "Fixture restricted route"}).click();
            await page.getByText("You do not have permission to view this page.").waitFor();
            await page.goBack();
            await page.getByRole("combobox", { name: "Time zone" }).waitFor();
            assert.equal(await page.evaluate(() => window.fixtureGeneration), generation);
            console.log(browserType.name() + " rendered and saved " + prefix + "settings/display without a circuit");
        }
        assert.ok(shared.some(([url, status]) => url.endsWith(".wasm") && status === 200), JSON.stringify(shared.slice(0, 4)));
        assert.ok(shared.some(([url, status]) => url.includes("/Quasar.Client.") && status === 404));
        assert.ok(fallbacks.some(status => status === 200 || status === 304));
        assert.deepEqual(circuits, []);
        assert.deepEqual(errors, []);
    } finally { await browser.close(); }
}
