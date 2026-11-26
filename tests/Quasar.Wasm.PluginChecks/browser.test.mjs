// Publishes the host before building the plugin. No Quasar worker or game server is started.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const fixture = path.dirname(fileURLToPath(import.meta.url));
const playwright = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : "playwright");
const { chromium, firefox } = playwright.default ?? playwright;
const output = await mkdtemp(path.join(tmpdir(), "quasar-wasm-plugin-"));
const host = path.join(output, "host", "wwwroot");
const plugin = path.join(output, "plugin", "ViewerFixture.dll");
const mime = { ".js": "text/javascript", ".html": "text/html", ".wasm": "application/wasm",
    ".json": "application/json", ".dll": "application/octet-stream", ".css": "text/css" };

async function check(browser, prefix) {
    const context = await browser.newContext();
    try {
        const page = await context.newPage();
        const errors = [];
        page.on("pageerror", error => errors.push(error.message));
        page.on("console", message => {
            if (message.type() === "error") errors.push(message.text());
        });
        let pluginRequests = 0;
        await page.route("**/*", async route => {
            const url = new URL(route.request().url());
            if (url.origin !== "https://panel.invalid" || !url.pathname.startsWith(prefix)) {
                return route.abort();
            }
            const relative = url.pathname.slice(prefix.length) || "index.html";
            const isPlugin = relative === "plugins/ViewerFixture.dll";
            if (isPlugin) pluginRequests++;
            try {
                const body = await readFile(isPlugin ? plugin : path.join(host, relative));
                await route.fulfill({ body, contentType: mime[path.extname(relative)] || "application/octet-stream" });
            } catch {
                await route.fulfill({ status: 404, body: "Missing fixture asset" });
            }
        });
        await page.goto("https://panel.invalid" + prefix);
        try {
            await page.getByRole("button", { name: "View Test grid" }).click({ timeout: 15000 });
            await page.getByText("Viewing Test grid", { exact: true }).waitFor({ timeout: 5000 });
        } catch (error) {
            throw new Error([error.message, ...errors].join("\n"), { cause: error });
        }
        assert.equal(pluginRequests, 1);
        const timeZone = page.getByRole("combobox", { name: "Time zone" });
        await timeZone.fill("Asia/Kolkata");
        await page.getByRole("button", { name: "Save", exact: true }).click();
        await page.getByText("Time zone saved.").waitFor({ timeout: 5000 });
        await page.reload();
        await timeZone.waitFor();
        assert.equal(await timeZone.inputValue(), "Asia/Kolkata");
        await timeZone.fill("Invalid/Zone");
        await page.getByRole("button", { name: "Save", exact: true }).click();
        await page.getByRole("alert").filter({ hasText: "Choose a valid time zone." }).waitFor();
        await page.getByRole("button", { name: "Use browser time" }).click();
        await page.getByText("Time zone saved.").waitFor();
        assert.equal(await timeZone.inputValue(), "");

        // Native dialog and toast host through the public UI contract.
        await page.goto("https://panel.invalid" + prefix + "?ui=1");
        await page.getByText("UI fixture (test plugin)").waitFor({ timeout: 15000 });
        await page.getByText("Native inline notice rendered by the fixture plugin.").waitFor({ timeout: 10000 });
        const filledNotice = page.locator(".quasar-notice-filled", {
            hasText: "Filled warning notice rendered by the fixture plugin.",
        });
        await filledNotice.waitFor({ state: "visible", timeout: 10000 });
        const filledColors = await filledNotice.evaluate(element => ({
            left: getComputedStyle(element).borderLeftColor,
            top: getComputedStyle(element).borderTopColor,
        }));
        assert.notEqual(filledColors.left, filledColors.top,
            "Filled notices must keep the severity accent on the left border");
        const accentNotice = page.locator(".quasar-notice", { hasText: "Accent override notice." });
        await accentNotice.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await accentNotice.evaluate(element => getComputedStyle(element).borderLeftColor),
            "rgb(1, 2, 3)", "An ancestor --quasar-notice-accent must override the severity accent");
        await page.getByRole("button", { name: "Open basic dialog" }).click();
        const basic = page.locator('dialog.quasar-dialog[aria-label="UI fixture dialog"]');
        await basic.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await basic.evaluate(element => element.open), true);
        const surfaceColors = await basic.evaluate(element => {
            const probe = document.createElement("span");
            probe.style.display = "none";
            probe.style.background = "var(--quasar-dialog-surface)";
            element.append(probe);
            const expected = getComputedStyle(probe).backgroundColor;
            const actual = getComputedStyle(element).backgroundColor;
            probe.remove();
            return { expected, actual };
        });
        assert.equal(surfaceColors.actual, surfaceColors.expected,
            `Dialog must use the resolved surface token; got ${surfaceColors.actual}`);
        assert.notEqual(surfaceColors.actual, "rgba(0, 0, 0, 0)");
        await page.keyboard.press("Escape");
        await basic.waitFor({ state: "hidden", timeout: 10000 });

        await page.getByRole("button", { name: "Open message box" }).click();
        const messageBox = page.locator('dialog.quasar-dialog[aria-label="Delete backup?"]');
        await messageBox.waitFor({ state: "visible", timeout: 10000 });
        await messageBox.getByRole("button", { name: "Close dialog" }).waitFor({ timeout: 10000 });
        const messageText = messageBox.locator("[data-quasar-dialog-description]");
        assert.equal(await messageText.evaluate(element => getComputedStyle(element).whiteSpace),
            "pre-line", "Message box text must preserve line breaks");
        const description = await messageBox.evaluate(element => {
            const id = element.getAttribute("aria-describedby");
            const target = id ? document.getElementById(id) : null;
            return {
                id,
                owned: target?.hasAttribute("data-quasar-dialog-description") ?? false,
                text: target?.textContent ?? null,
            };
        });
        assert.ok(description.id);
        assert.equal(description.owned, true);
        assert.equal(description.text, "Permanently delete it?");
        await messageBox.getByRole("button", { name: "Delete", exact: true }).click();
        await page.getByText("Message box: True").waitFor({ timeout: 5000 });

        await page.getByRole("button", { name: "Open sync dialog" }).click();
        const sync = page.locator('dialog.quasar-dialog[aria-label="Sync dialog"]');
        await sync.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await sync.evaluate(element => element.open), true);
        await page.keyboard.press("Escape");
        await sync.waitFor({ state: "hidden", timeout: 10000 });

        // A description rendered after the dialog opens must still become the
        // accessible description.
        await page.getByRole("button", { name: "Open late description" }).click();
        const late = page.locator('dialog.quasar-dialog[aria-label="Late description dialog"]');
        await late.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await late.getAttribute("aria-describedby"), null);
        await late.getByRole("button", { name: "Add description" }).click();
        await page.waitForFunction(() => {
            const dialog = [...document.querySelectorAll("dialog.quasar-dialog")]
                .find(element => element.getAttribute("aria-label") === "Late description dialog");
            const id = dialog?.getAttribute("aria-describedby");
            return (id ? document.getElementById(id)?.textContent : null)
                ?.includes("Late description added after open.") ?? false;
        }, null, { timeout: 10000 });
        const lateDescription = await late.evaluate(element => {
            const id = element.getAttribute("aria-describedby");
            return id ? document.getElementById(id)?.textContent ?? null : null;
        });
        assert.equal(lateDescription, "Late description added after open.");
        await page.keyboard.press("Escape");
        await late.waitFor({ state: "hidden", timeout: 10000 });

        // A locked modal must swallow Escape on every press and remain open
        // until a control closes it.
        await page.getByRole("button", { name: "Open locked dialog" }).click();
        const locked = page.locator('dialog.quasar-dialog[aria-label="Locked dialog"]');
        await locked.waitFor({ state: "visible", timeout: 10000 });
        await page.keyboard.press("Escape");
        await page.keyboard.press("Escape");
        assert.equal(await locked.evaluate(element => element.open), true,
            "Escape must not close a locked dialog");
        await locked.getByRole("button", { name: "Cancel", exact: true }).click();
        await locked.waitFor({ state: "hidden", timeout: 10000 });

        // The per-invocation surface override must resolve exactly.
        await page.getByRole("button", { name: "Open custom dialog" }).click();
        const custom = page.locator("dialog.ui-fixture-dialog-accent").first();
        await custom.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(
            await custom.evaluate(element => getComputedStyle(element).backgroundColor),
            "rgb(58, 47, 92)");
        await page.keyboard.press("Escape");
        await custom.waitFor({ state: "hidden", timeout: 10000 });

        await page.getByRole("button", { name: "Open modeless dialog" }).click();
        const modeless = page.locator(".quasar-dialog-modeless").first();
        await modeless.waitFor({ state: "visible", timeout: 10000 });
        await page.getByRole("button", { name: "Toast: Normal" }).click();
        await page.locator(".quasar-toast", { hasText: "Native Normal toast from the fixture plugin." })
            .waitFor({ state: "visible", timeout: 10000 });
        const beforeDrag = await modeless.boundingBox();
        const dragHandle = page.locator(".quasar-dialog-drag-handle").first();
        const handleBox = await dragHandle.boundingBox();
        await page.mouse.move(handleBox.x + handleBox.width / 2, handleBox.y + handleBox.height / 2);
        await page.mouse.down();
        await page.mouse.move(handleBox.x + handleBox.width / 2 + 60, handleBox.y + handleBox.height / 2 + 30);
        await page.mouse.up();
        const afterDrag = await modeless.boundingBox();
        assert.notEqual(beforeDrag.x, afterDrag.x);
        assert.notEqual(beforeDrag.y, afterDrag.y);
        await page.getByRole("button", { name: "Cancel", exact: true }).click();
        await modeless.waitFor({ state: "hidden", timeout: 10000 });

        await page.getByRole("button", { name: "Toast: Success" }).click();
        const toast = page.locator(".quasar-toast", { hasText: "Native Success toast from the fixture plugin." });
        await toast.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await toast.getAttribute("role"), "status");

        // The dialog must size to its content by default, and FullWidth must
        // still respect MaxWidth instead of filling the viewport.
        const widths = await page.evaluate(() => {
            const measure = extraClass => {
                const dialog = document.createElement("dialog");
                dialog.className = "quasar-dialog quasar-dialog-width-sm " + extraClass;
                dialog.style.setProperty("--quasar-dialog-max-width", "600px");
                dialog.innerHTML = "<p>size</p>";
                document.body.append(dialog);
                dialog.show();
                const width = dialog.getBoundingClientRect().width;
                dialog.close();
                dialog.remove();
                return width;
            };
            return { content: measure(""), full: measure("quasar-dialog-full-width") };
        });
        assert.ok(widths.content < 600, "Default dialog must size to content; got " + widths.content);
        assert.ok(widths.full <= 601, "FullWidth must respect MaxWidth; got " + widths.full);

        // Newest-on-top must remain reachable when the stack overflows, and the
        // top-anchored host must not extend below the viewport.
        const oldViewport = page.viewportSize() ?? { width: 1280, height: 720 };
        await page.setViewportSize({ width: 360, height: 640 });
        const toastLayout = await page.evaluate(() => {
            const shell = document.querySelector(".quasar-shell") || document.body;
            const host = document.createElement("div");
            host.className = "quasar-toast-host quasar-toast-host-topright";
            for (let index = 0; index < 5; index++) {
                const item = document.createElement("div");
                item.className = "quasar-toast";
                item.style.order = String(-index);
                item.style.animation = "none";
                const message = document.createElement("div");
                message.className = "quasar-toast-message";
                message.textContent = "Long toast " + index + " " + "x".repeat(1000);
                item.append(message);
                host.append(item);
            }
            shell.append(host);
            const hostRect = host.getBoundingClientRect();
            const newestRect = host.children[4].getBoundingClientRect();
            const result = {
                hostTop: hostRect.top,
                hostBottom: hostRect.bottom,
                hostClientHeight: host.clientHeight,
                hostScrollHeight: host.scrollHeight,
                scrollTop: host.scrollTop,
                newestTop: newestRect.top,
                newestBottom: newestRect.bottom,
                viewportHeight: innerHeight,
            };
            host.remove();
            return result;
        });
        await page.setViewportSize(oldViewport);
        assert.ok(toastLayout.hostScrollHeight > toastLayout.hostClientHeight,
            "toast stack must overflow for this check");
        assert.ok(toastLayout.newestTop >= toastLayout.hostTop - 1,
            "newest toast must be inside the host");
        assert.ok(toastLayout.newestBottom <= toastLayout.viewportHeight,
            "newest toast must fit in the viewport");
        assert.ok(toastLayout.hostBottom <= toastLayout.viewportHeight,
            "top toast host must not extend below the viewport");

        assert.deepEqual(errors, []);
        console.log(browser.browserType().name() + ": independent Razor plugin and Display settings passed at " + prefix);
    } finally {
        await context.close();
    }
}

try {
    execFileSync("dotnet", ["publish", "Client/Client.csproj", "-c", "Release", "-o", path.dirname(host)],
        { cwd: fixture, stdio: "inherit" });
    execFileSync("dotnet", ["build", "Plugin/ViewerFixture.csproj", "-c", "Release", "-o", path.dirname(plugin)],
        { cwd: fixture, stdio: "inherit" });
    for (const browserType of (process.env.QUASAR_BROWSER === "chromium" ? [chromium] : [chromium, firefox])) {
        const browser = await browserType.launch(browserType === chromium ? { executablePath: process.env.CHROME_PATH } : {});
        try {
            await check(browser, "/");
            await check(browser, "/test-panel/");
        } finally {
            await browser.close();
        }
    }
} finally {
    await rm(output, { recursive: true, force: true });
}
