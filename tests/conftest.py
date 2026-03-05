import os
import subprocess
import time

import pytest
import requests
from playwright.sync_api import sync_playwright, Browser, Page, BrowserContext

BASE_URL = os.environ.get("BASE_URL", "https://host.docker.internal:5001")
HEADLESS = os.environ.get("HEADLESS", "true").lower() == "true"
SLOW_MO = int(os.environ.get("SLOW_MO", "0"))
MOCK_LLM_PORT = int(os.environ.get("MOCK_LLM_PORT", "5555"))


@pytest.fixture(scope="session")
def mock_llm_server():
    """Start mock LLM server for AI chat tests."""
    proc = subprocess.Popen(
        ["python", "mock_llm/server.py", "--port", str(MOCK_LLM_PORT)],
        cwd=os.path.dirname(os.path.abspath(__file__)),
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    # Wait for server to become healthy
    for _ in range(30):
        try:
            resp = requests.get(f"http://localhost:{MOCK_LLM_PORT}/health")
            if resp.status_code == 200:
                break
        except Exception:
            time.sleep(0.5)
    else:
        proc.terminate()
        raise RuntimeError(f"Mock LLM server failed to start on port {MOCK_LLM_PORT}")
    yield proc
    proc.terminate()
    try:
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        proc.kill()


@pytest.fixture(scope="session")
def browser():
    """Session-scoped browser instance."""
    with sync_playwright() as p:
        browser = p.chromium.launch(headless=HEADLESS, slow_mo=SLOW_MO)
        yield browser
        browser.close()


@pytest.fixture(scope="function")
def context(browser: Browser):
    """Function-scoped browser context with fresh state per test."""
    ctx = browser.new_context(
        viewport={"width": 1920, "height": 1080},
        ignore_https_errors=True,
    )
    ctx.set_default_timeout(30000)
    yield ctx
    ctx.close()


@pytest.fixture(scope="function")
def page(context: BrowserContext) -> Page:
    """Function-scoped page that navigates to the app and waits for XAF to load."""
    pg = context.new_page()
    pg.goto(BASE_URL, wait_until="networkidle", timeout=60000)
    # Wait for XAF navigation to appear (accordion with nav items)
    pg.wait_for_selector(".xaf-nav-link", timeout=60000)
    # Extra wait for first circuit initialization (Roslyn compilation on cold start)
    pg.wait_for_timeout(2000)
    yield pg
    pg.close()
