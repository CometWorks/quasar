import assert from "node:assert/strict";

export async function checkViewer(page, base) {
    let delivery;
    const record = r => { if (r.url().includes("/sdk/v1/ui/assemblies/")) delivery = r; };
    page.on("response", record);
    try {
        await page.goto(base + "entities");
        const row = page.getByRole("row").filter({ hasText: "Fixture freighter" });
        await row.getByText("9007199254740993", { exact: true }).waitFor();
        const view = row.locator(".entity-viewer-icon-button");
        await view.waitFor();
        assert.ok(await view.isEnabled());
        if (base.endsWith("/fixture-panel/") && process.env.QUASAR_RTC_TEST_PEER) {
            await page.waitForFunction(async () => (await quasarSdk.transportStatus()) === "direct", null, {timeout: 30_000});
            const before = await page.evaluate(async () => (await quasarSdk.getClient()).transport.receivedBytes);
            await page.getByRole("button", { name: /refresh/i }).first().click();
            await page.waitForFunction(async before => (await quasarSdk.getClient()).transport.receivedBytes > before, before);
            await row.getByText("9007199254740993", {exact: true}).waitFor();
            console.log("Authorized Viewer entity data reached the hosted panel over direct WebRTC.");
        }
        const transfer = await page.evaluate(async () => {
            const sdk = await quasarSdk.getClient();
            const plugin = "cometworks.entityviewer";
            const scene = await sdk.request(plugin, "scene.read", {serverId: "viewer-node", entityId: "9007199254740993"});
            const asset = await sdk.request(plugin, "assets.resolve", {sessionId: scene.assetSession.sessionId, asset: {logicalPath: "Models/fixture.mwm"}});
            if (!asset.found) throw new Error("The Viewer fixture model could not be resolved.");
            const received = sdk.transport.receivedBytes;
            const handle = await sdk.openRead(plugin, "assets.file.read", {assetToken: asset.assetToken});
            try {
                const bytes = new Uint8Array(await new Response(handle.body).arrayBuffer());
                return {path: sdk.transport.path, bytes: bytes.length, valid: bytes.every((v,i) => v === i % 251),
                    peerBytes: sdk.transport.receivedBytes - received};
            } finally { await handle.dispose(); }
        });
        assert.equal(transfer.bytes, 2 * 1024 * 1024);
        assert.ok(transfer.valid);
        if (base.endsWith("/fixture-panel/") && process.env.QUASAR_RTC_TEST_PEER) {
            assert.equal(transfer.path, "direct"); assert.ok(transfer.peerBytes >= transfer.bytes);
        } else { assert.equal(transfer.path, "http"); }
        console.log("Verified " + transfer.bytes + " Viewer model bytes on " + transfer.path + " at " + base);
        await view.click();
        await page.getByRole("dialog").waitFor();
        await page.keyboard.press("Escape");
        assert.ok(delivery?.url().startsWith(base + "_quasar/sdk/v1/ui/assemblies/cometworks.entityviewer/"));
        assert.equal(delivery.status(), 200);
        assert.equal((await delivery.allHeaders())["cache-control"], "private, no-store");
        const anonymous = await page.context().browser().newContext({ignoreHTTPSErrors: true});
        try { assert.equal((await anonymous.request.get(delivery.url())).status(), 401); }
        finally { await anonymous.close(); }
    } finally { page.off("response", record); }
}
