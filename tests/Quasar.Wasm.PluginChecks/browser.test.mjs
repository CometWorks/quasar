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
        await page.getByRole("button", { name: "Open basic dialog" }).click();
        const surface = page.locator("dialog.quasar-dialog").first();
        await surface.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await surface.evaluate(element => element.open), true);
        assert.match(await surface.evaluate(element => getComputedStyle(element).backgroundColor), /^rgb/);
        await page.keyboard.press("Escape");
        await surface.waitFor({ state: "hidden", timeout: 10000 });

        await page.getByRole("button", { name: "Open sync dialog" }).click();
        await surface.waitFor({ state: "visible", timeout: 10000 });
        assert.equal(await surface.evaluate(element => element.open), true);
        await page.keyboard.press("Escape");
        await surface.waitFor({ state: "hidden", timeout: 10000 });

        await page.getByRole("button", { name: "Open modeless dialog" }).click();
        const modeless = page.locator(".quasar-dialog-modeless").first();
        await modeless.waitFor({ state: "visible", timeout: 10000 });
        await page.getByRole("button", { name: "Toast: Normal" }).click();
        await page.locator(".quasar-toast").first().waitFor({ state: "visible", timeout: 10000 });
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
        const toast = page.locator(".quasar-toast").first();
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
