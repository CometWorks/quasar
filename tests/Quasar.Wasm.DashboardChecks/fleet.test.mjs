import assert from "node:assert/strict";

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
        assert.deepEqual(errors, []);
        assert.deepEqual(circuits, []);
        console.log(browser.browserType().name() + " switched fleet layouts, opened standalone live data, the log console and the server editor, and resumed the setup guide at " + base);
    } finally { await context.close(); }
}
