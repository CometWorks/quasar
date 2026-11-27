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
const { chromium, firefox, webkit } = playwright.default ?? playwright;
const output = await mkdtemp(path.join(tmpdir(), "quasar-wasm-plugin-"));
const host = path.join(output, "host", "wwwroot");
const plugin = path.join(output, "plugin", "ViewerFixture.dll");
const mime = { ".js": "text/javascript", ".html": "text/html", ".wasm": "application/wasm",
    ".json": "application/json", ".dll": "application/octet-stream", ".css": "text/css" };

// Exercises Quasar.Ui.Components from a plugin that references only
// Quasar.Ui.Abstractions, including popup controls inside a native modal dialog.
async function checkUiKit(page, prefix) {
    // A tooltip is shown when a hit test at its centre lands on it. That is what a
    // user can see and point at, and it does not depend on getComputedStyle, which
    // Firefox reports late for anchor-positioned elements.
    const isShown = locator => locator.evaluate(element => {
        const box = element.getBoundingClientRect();
        const hit = document.elementFromPoint(box.x + box.width / 2, box.y + box.height / 2);
        return hit !== null && element.contains(hit);
    });
    const waitForTooltip = async (locator, shown) => {
        for (let attempt = 0; attempt < 40 && await isShown(locator) !== shown; attempt++)
            await page.waitForTimeout(50);
        assert.equal(await isShown(locator), shown, shown ? "The tooltip must be shown" : "The tooltip must be hidden");
    };
    const scrollTop = () => page.evaluate(() => document.scrollingElement.scrollTop);

    await page.goto("https://panel.invalid" + prefix + "?ui=kit");
    await page.getByRole("heading", { name: "Native UI kit (test plugin)" }).waitFor({ timeout: 15000 });
    const formState = page.getByTestId("form-state");
    assert.equal((await formState.textContent()).trim(), "valid / clean");

    // Fields commit on change, so a fill is followed by a blur.
    const name = page.getByLabel("Server name");
    await name.fill("Alpha");
    await name.blur();
    await page.getByText("valid / dirty").waitFor();

    await name.fill("not valid");
    await name.blur();
    await page.getByText("Use a name without spaces.").waitFor();
    assert.equal(await name.getAttribute("aria-invalid"), "true");
    await name.fill("Alpha");
    await name.blur();
    await page.getByText("Use a name without spaces.").waitFor({ state: "hidden" });

    const port = page.getByLabel("Port");
    await port.fill("70000");
    await port.blur();
    await page.waitForFunction(() => document.getElementById(
        [...document.querySelectorAll("label")].find(label => label.textContent.trim().startsWith("Port")).htmlFor).value === "65535");

    // A whole-number field ignores the keys that can only make a decimal, an
    // exponent or a minus sign (the minimum is 1, so negatives are out too).
    await port.fill("");
    await port.pressSequentially("7.5e+-1,2");
    assert.equal(await port.inputValue(), "7512");
    assert.equal(await port.getAttribute("step"), "1");
    await port.blur();
    await page.waitForFunction(() => document.getElementById(
        [...document.querySelectorAll("label")].find(label => label.textContent.trim().startsWith("Port")).htmlFor).value === "7512");

    await page.getByLabel("Region").first().selectOption({ label: "North America" });
    await page.getByLabel("Balanced").check();
    await page.getByLabel("Auto restart").check();
    await page.getByRole("button", { name: "Validate and save" }).click();
    await page.getByText("Saved.", { exact: true }).waitFor();
    await page.getByText("valid / clean").waitFor();
    assert.equal(await page.getByLabel("Region").first().inputValue(), "1");

    // A link with an unsafe address is a disabled link, not a followable one.
    const unsafe = page.locator("a", { hasText: "Unsafe link" });
    assert.equal(await unsafe.getAttribute("aria-disabled"), "true");
    assert.equal(await unsafe.getAttribute("href"), null);
    assert.equal(await page.locator('a[href^="javascript"]').count(), 0);

    // The menu opens beside its trigger, takes focus, and follows the keyboard.
    const trigger = page.getByRole("button", { name: "Row actions" });
    await trigger.click();
    const menu = page.locator('[role="menu"][aria-label="Row actions"]');
    await menu.waitFor({ state: "visible" });
    const triggerBox = await trigger.boundingBox();
    const menuBox = await menu.boundingBox();
    const gap = Math.min(
        Math.abs(menuBox.y - (triggerBox.y + triggerBox.height)),
        Math.abs(menuBox.y + menuBox.height - triggerBox.y));
    assert.ok(gap < 24, `Menu must open beside its trigger; gap ${gap}`);
    assert.ok(menuBox.x < triggerBox.x + 300 && menuBox.x + menuBox.width > triggerBox.x - 300,
        "Menu must open horizontally near its trigger");
    // The browser raises the toggle event shortly after the popover changes.
    const settle = async (read, expected, message) => {
        for (let attempt = 0; attempt < 40 && await read() !== expected; attempt++)
            await page.waitForTimeout(50);
        assert.equal(await read(), expected, message);
    };
    const expanded = () => trigger.getAttribute("aria-expanded");
    await settle(expanded, "true", "Opening the menu must set aria-expanded");
    const activeText = () => page.evaluate(() => document.activeElement?.textContent?.trim());
    await settle(activeText, "Edit", "Opening the menu must focus its first item");
    await page.keyboard.press("ArrowDown");
    assert.equal(await activeText(), "Copy");
    await page.keyboard.press("ArrowDown");
    assert.equal(await activeText(), "Edit", "Arrow keys must skip the disabled item and wrap");
    await page.keyboard.press("Escape");
    await menu.waitFor({ state: "hidden" });
    await settle(expanded, "false", "Closing the menu must clear aria-expanded");
    assert.equal(await page.evaluate(() => document.activeElement?.getAttribute("aria-label")), "Row actions");

    await trigger.click();
    await page.getByRole("menuitem", { name: "Copy" }).click();
    await page.getByText("Copy chosen.").waitFor();

    // Tabs move with the arrow, Home and End keys and do not scroll the page.
    const overview = page.getByRole("tab", { name: "Overview" });
    await overview.scrollIntoViewIfNeeded();
    await page.evaluate(() => document.scrollingElement.scrollBy(0, 120));
    await overview.focus();
    const before = await scrollTop();
    assert.ok(before > 0, "The page must be scrolled for the key check");
    await page.keyboard.press("ArrowRight");
    await page.getByText("Activity panel.").waitFor();
    await page.keyboard.press("End");
    await page.getByText("Settings panel.").waitFor();
    await page.keyboard.press("Home");
    await page.getByText("Overview panel.").waitFor();
    assert.ok(Math.abs(await scrollTop() - before) < 4, "Tab keys must not scroll the page");
    assert.equal(await page.getByRole("tab", { name: "Overview" }).getAttribute("aria-selected"), "true");

    await page.getByText("Advanced settings").click();
    await page.getByText("Disclosure content is hidden until the summary is activated.").waitFor();

    // The tooltip shows on hover, is dismissed by Escape, and shows again on a new hover.
    const tooltipTarget = page.getByRole("button", { name: "Hover me" });
    const tooltip = page.locator('[role="tooltip"]', { hasText: "Copies the value to the clipboard" });
    // Earlier steps leave the pointer wherever the last click was; a tooltip that
    // Escape dismissed while hovered stays dismissed until the pointer leaves.
    await page.mouse.move(0, 0);
    await waitForTooltip(tooltip, false);
    await tooltipTarget.hover();
    await waitForTooltip(tooltip, true);
    await page.keyboard.press("Escape");
    await waitForTooltip(tooltip, false);
    await page.mouse.move(0, 0);
    await tooltipTarget.hover();
    await waitForTooltip(tooltip, true);
    await page.mouse.move(0, 0);

    // An Immediate field commits after a pause and filters the table.
    const search = page.getByLabel("Search servers");
    await search.fill("Delta");
    const sampleTable = page.getByRole("table", { name: "Sample servers" });
    await sampleTable.getByText("Delta").waitFor();
    await sampleTable.getByText("Bravo").waitFor({ state: "hidden" });
    await search.fill("");
    await sampleTable.getByText("Bravo").waitFor();

    // Sorting and paging.
    await sampleTable.getByRole("button", { name: "Port" }).click();
    assert.equal(await sampleTable.locator("th", { hasText: "Port" }).getAttribute("aria-sort"), "ascending");
    await page.getByRole("navigation", { name: "Table pagination" }).first()
        .getByRole("button", { name: "Next page" }).click();
    await sampleTable.getByText("Hotel").waitFor();

    // The stacked table turns into labelled cards below its breakpoint. The
    // default table keeps its columns and scrolls instead.
    const stacked = page.getByRole("table", { name: "Stacked servers" });
    const cellDisplay = locator => locator.locator("tbody td").first().evaluate(
        element => getComputedStyle(element).display);
    const cellLabel = locator => locator.locator("tbody td").first().evaluate(
        element => getComputedStyle(element, "::before").content);
    const viewport = page.viewportSize();
    await page.setViewportSize({ width: 390, height: 900 });
    assert.equal(await cellDisplay(stacked), "flex");
    assert.equal(await cellLabel(stacked), '"Name"');
    assert.equal(await cellDisplay(sampleTable), "table-cell");
    await page.setViewportSize({ width: 1440, height: 900 });
    assert.equal(await cellDisplay(stacked), "table-cell");
    if (process.env.QUASAR_SHOTS) {
        const engine = page.context().browser().browserType().name();
        const shot = (width) => path.join(process.env.QUASAR_SHOTS,
            `ui-kit-${engine}-${prefix === "/" ? "root" : "prefixed"}-${width}.png`);
        await page.screenshot({ path: shot(1440), fullPage: true });
        await page.setViewportSize({ width: 390, height: 900 });
        await page.screenshot({ path: shot(390), fullPage: true });
    }
    await page.setViewportSize(viewport);

    // Each control owns its classes; the older hand-written markup keeps its own.
    // A span with the legacy tooltip class must still be layout-neutral.
    assert.equal(await page.evaluate(() => {
        const span = document.createElement("span");
        span.className = "quasar-tooltip";
        document.body.append(span);
        const display = getComputedStyle(span).display;
        span.remove();
        return display;
    }), "contents");

    // Popup controls work inside a native modal dialog.
    await page.getByRole("button", { name: "Open kit dialog" }).click();
    const dialog = page.locator('dialog.quasar-dialog[aria-label="Kit dialog"]');
    await dialog.waitFor({ state: "visible" });
    await dialog.getByLabel("Dialog region").selectOption({ label: "North America" });
    assert.equal(await dialog.getByLabel("Dialog region").inputValue(), "1");

    await dialog.getByRole("button", { name: "Dialog actions" }).click();
    const dialogMenu = dialog.getByRole("menuitem", { name: "Edit" });
    await dialogMenu.waitFor({ state: "visible" });
    await dialogMenu.click();
    await dialog.getByText("Dialog edit chosen.").waitFor();

    const dialogTooltip = page.locator('[role="tooltip"]', { hasText: "Tooltip inside a dialog" });
    await dialog.getByRole("button", { name: "Dialog tooltip" }).hover();
    await waitForTooltip(dialogTooltip, true);
    await page.mouse.move(0, 0);

    await dialog.getByRole("button", { name: "Close" }).click();
    await dialog.waitFor({ state: "hidden" });
    console.log("UI kit checks passed at " + prefix);
}

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

        await checkUiKit(page, prefix);

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
    // QUASAR_BROWSER is a comma-separated list of chromium, firefox and webkit.
    // Without it the checks run on Chromium and Firefox.
    const available = { chromium, firefox, webkit };
    const requested = (process.env.QUASAR_BROWSER ?? "chromium,firefox").split(",").map(name => name.trim());
    for (const browserType of requested.map(name => available[name] ?? assert.fail("Unknown browser " + name))) {
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
