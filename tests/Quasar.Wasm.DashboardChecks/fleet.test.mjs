import assert from "node:assert/strict";
import {join} from "node:path";

export async function checkFleet(browser, base) {
    const context = await browser.newContext();
    try {
        const page = await context.newPage();
        const errors = [], circuits = [];
        page.on("pageerror", error => errors.push(error.message));
        page.on("request", request => { if (request.url().includes("/_blazor")) circuits.push(request.url()); });
        await context.request.get(base + "fixture/fleet-login");
        await page.goto(base);
        const fleet = page.getByRole("heading", {name: "Fleet overview"});
        await fleet.waitFor();
        await page.locator(".quasar-server-name-cell").filter({hasText: "Europa fleet fixture"}).waitFor();
        await page.getByRole("button", {name: "Cards", exact: true}).click();
        await page.locator(".quasar-card-grid .quasar-server-identity").filter({hasText: "Europa fleet fixture"}).waitFor();
        assert.equal(await page.locator(".quasar-server-table").count(), 0);
        await page.getByRole("button", {name: "List", exact: true}).click();
        await page.locator(".quasar-server-name-cell").filter({hasText: "Europa fleet fixture"}).waitFor();
        await page.getByRole("button", {name: "Show live data"}).click();
        await page.getByRole("heading", {name: "Recent Chat",exact:true}).waitFor();
        await page.getByRole("button", {name: "Show console output"}).click();
        await page.getByRole("region", {name: "Server log output"}).getByText("Fixture server log entry").waitFor();
        await page.keyboard.press("Escape");
        await page.getByRole("button", {name: "More actions for Europa fleet fixture"}).click();
        await page.getByRole("button", {name: "Edit server", exact:true}).click();
        const editor = page.getByRole("dialog");
        await editor.getByLabel("Server name", {exact:false}).first().waitFor();
        assert.equal(await editor.getByLabel("Server name", {exact:false}).first().inputValue(), "Europa fleet fixture");
        await editor.getByRole("button", {name: "Cancel", exact:true}).click();
        await page.getByRole("button", {name: "Setup guide"}).click();
        const dialog = page.getByRole("dialog");
        await dialog.getByRole("button", {name: "Single Magnetar"}).click();
        await page.getByText("Step 1 of 5", {exact: false}).waitFor();
        await page.reload();
        await page.getByText("Step 1 of 5", {exact: false}).waitFor();
        await page.getByRole("button", {name: "Skip setup"}).click();
        await page.getByRole("button", {name: "Import server", exact: true}).click();
        const importer = page.getByRole("dialog");
        await importer.getByRole("button", {name: "Vanilla Dedicated Server"}).click();
        await importer.getByRole("button", {name: "Next", exact: true}).click();
        await importer.getByRole("button", {name: "Upload ZIP", exact: true}).click();
        await importer.locator("input[type=file]").setInputFiles(join(process.env.QUASAR_INSTALL_DIR, "selective-server.zip"));
        const direct = base.endsWith("/fixture-panel/") && !!process.env.QUASAR_RTC_TEST_PEER;
        if (direct) await page.waitForFunction(async () => (await quasarSdk.transportStatus()) === "direct");
        await page.evaluate(() => {
            window.fixtureZipBytes = 0;
            const slice = File.prototype.slice;
            File.prototype.slice = function(start, end, type) {
                const part = slice.call(this, start, end, type);
                if (this.name === "selective-server.zip") window.fixtureZipBytes += part.size;
                return part;
            };
        });
        let httpUploads = 0;
        const record = request => {
            if (request.method() === "PUT" && request.url().includes("/sdk/v1/resources/"))
                httpUploads++;
        };
        page.on("request", record);
        const before = await page.evaluate(async () => (await quasarSdk.getClient()).transport.sentBytes);
        await importer.getByRole("button", {name:"Analyze",exact:true}).click();
        await importer.getByText("Review detected data", {exact:true}).waitFor();
        const transferred = await page.evaluate(() => window.fixtureZipBytes);
        const peerBytes = await page.evaluate(async () => (await quasarSdk.getClient()).transport.sentBytes) - before;
        page.off("request", record);
        assert.ok(transferred >= 256 * 1024 && transferred < 320 * 1024, "Unexpected source ZIP bytes " + transferred);
        if (direct) {
            assert.equal(httpUploads, 0);
            assert.ok(peerBytes >= 256 * 1024 && peerBytes < 512 * 1024, "Unexpected direct upload bytes " + peerBytes);
        } else assert.ok(httpUploads > 0, "Expected file ranges through the HTTP SDK");
        await importer.getByRole("button", {name:"Cancel",exact:true}).click();
        console.log(browser.browserType().name() + " reviewed a 4.25 MiB server ZIP using " + transferred + " bytes over " + (direct ? "WebRTC" : base.endsWith("/fixture-panel/") ? "the hosted HTTP fallback" : "local HTTP"));
        assert.deepEqual(errors, []);
        assert.deepEqual(circuits, []);
        console.log(browser.browserType().name() + " switched fleet layouts, opened standalone live data, the log console and the server editor, and resumed the setup guide at " + base);
    } finally { await context.close(); }
}
