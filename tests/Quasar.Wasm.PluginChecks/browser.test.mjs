// Publishes the host before building the plugin. No Quasar worker or game server is started.
import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const fixture = path.dirname(fileURLToPath(import.meta.url));
const { chromium, firefox } = await import(process.env.PLAYWRIGHT_MODULE
    ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : "playwright");
const output = await mkdtemp(path.join(tmpdir(), "quasar-wasm-plugin-"));
const host = path.join(output, "host", "wwwroot");
const plugin = path.join(output, "plugin", "ViewerFixture.dll");
const mime = { ".js": "text/javascript", ".html": "text/html", ".wasm": "application/wasm",
    ".json": "application/json", ".dll": "application/octet-stream" };

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
    for (const browserType of [chromium, firefox]) {
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
