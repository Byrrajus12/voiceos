#!/usr/bin/env python3
"""Generates pass3.json: targeted follow-up-reference workflows for the typed referent store.

Browser fixture scenarios need the fixture site: dotnet tests/.../VoiceOS.Eval.dll fixtures
Chrome + Companion must be connected. Native scenarios open and close Notepad / Paint.
"""
import json, os

SITE = "http://localhost:18777"
TAGS = ["pass3", "multi-turn", "reference"]


def page_setup(path):
    return [
        {"kind": "startProcess", "file": "chrome.exe", "args": f"{SITE}{path}"},
        {"kind": "wait", "ms": 2500},
        {"kind": "focusWindow", "process": "chrome"},
    ]


def page_pre(origin="localhost:18777"):
    return [{"kind": "companionConnected"}, {"kind": "foregroundProcess", "process": "chrome"},
            {"kind": "activeTabOrigin", "contains": origin}]


def turn(text, direct=None, url=None, origin=None, scope=None, outcome="Complete", new_tabs=None, windows=None, before=None,
         accepted=None, settle=None, fg=None):
    expect = {"outcome": outcome}
    if accepted:
        expect["acceptedOutcomes"] = accepted
    if direct:
        expect["directSteps"] = {"include": direct}
    if scope:
        expect["scope"] = scope
    if new_tabs is not None:
        expect["newTabs"] = new_tabs
    final = {}
    if url:
        final["activeTabUrlContains"] = url
    if origin:
        final["activeTabOriginContains"] = origin
    if windows:
        final["windows"] = windows
    if fg:
        final["foregroundProcess"] = fg
    if final:
        expect["final"] = final
    t = {"transcript": text, "expect": expect}
    if before:
        t["before"] = before
    if settle:
        t["settleMs"] = settle
    return t


def scenario(id_, name, tags, turns, setup=None, pre=None, timeout=90, cleanup=None):
    s = {"id": id_, "name": name, "family": "pass3-referents", "tags": TAGS + tags,
         "preconditions": pre if pre is not None else [{"kind": "companionConnected"}],
         "timeoutSeconds": timeout, "turns": turns}
    if setup:
        s["setup"] = setup
    s["cleanup"] = cleanup if cleanup is not None else [{"kind": "closeNewTabs"}]
    return s


CLOSE_NEW = [{"kind": "closeNewTabs"}]
SHOP = [{"kind": "startProcess", "file": "chrome.exe", "args": f"{SITE}/shop"}, {"kind": "wait", "ms": 2500}, {"kind": "focusWindow", "process": "chrome"}]
FOCUS_CHROME = [{"kind": "focusWindow", "process": "chrome"}]
NOTE = {"process": "notepad"}

out = [
    # ---- Browser (fixture site) -------------------------------------------------------------
    scenario("p3.open-that-again", "Open a product, move on, open that one again", ["browser", "fixture", "item"], [
        turn("Show me the Ember Camp Stove.", url="shop/p/ember-camp-stove"),
        turn("Show me the Drift Sleeping Bag.", url="shop/p/drift-sleeping-bag", before=SHOP),
        turn("Show me the camp stove page again.", url="shop/p/ember-camp-stove"),
    ], setup=page_setup("/shop"), pre=page_pre()),
    scenario("p3.other-one", "Two concrete products, then the other one", ["browser", "fixture", "other"], [
        turn("Show me the Ember Camp Stove.", url="shop/p/ember-camp-stove"),
        turn("Show me the Drift Sleeping Bag.", url="shop/p/drift-sleeping-bag", before=SHOP),
        turn("Now show me the other one.", url="shop/p/ember-camp-stove"),
    ], setup=page_setup("/shop"), pre=page_pre()),
    scenario("p3.property-one", "Property-described alternative among three products", ["browser", "fixture", "property"], [
        turn("Show me the Alpine Trail Backpack.", url="shop/p/alpine-trail-backpack"),
        turn("Show me the Ridge Lightweight Tent.", url="shop/p/ridge-lightweight-tent", before=SHOP),
        turn("Show me the Beacon Headlamp.", url="shop/p/beacon-headlamp", before=SHOP),
        turn("Show me the tent one again.", url="shop/p/ridge-lightweight-tent"),
    ], setup=page_setup("/shop"), pre=page_pre()),
    scenario("p3.bring-page-back", "Bring an earlier page back after opening another tab", ["browser", "fixture", "return"], [
        turn("Open the API reference.", url="docs/api"),
        turn(f"Open {SITE}/news in a new tab.", url="/news"),
        turn("Bring that API reference page back up.", url="docs/api", new_tabs={"max": 0}),
    ], setup=page_setup("/docs"), pre=page_pre()),
    scenario("p3.stale-closed-tab", "Reference to a page whose tab was closed", ["browser", "fixture", "stale"], [
        turn(f"Open {SITE}/docs/faq in a new tab.", url="docs/faq"),
        turn("Bring that FAQ page back up.", before=CLOSE_NEW,
             outcome="Complete", accepted=["Complete", "Clarify"], new_tabs={"max": 1}, url="docs/faq"),
    ], setup=page_setup("/news"), pre=page_pre()),
    scenario("p3.current-vs-stored", "Visible link wins over an older, similarly named stored page", ["browser", "fixture", "precedence"], [
        turn("Show me the Ember Camp Stove.", url="shop/p/ember-camp-stove"),
        turn("Show me the Ember Camp Stove.", url="shop/p/ember-camp-stove", before=SHOP, scope=["ActiveTab"]),
    ], setup=page_setup("/shop"), pre=page_pre()),
    # ---- Native (disposable windows only: Paint, Character Map) ---------------------------------
    scenario("p3.native-snap-then-close", "Open Paint, snap it left, close it", ["native"], [
        turn("Open Paint.", settle=2500, windows=[{"process": "mspaint"}]),
        turn("Snap it to the left.", windows=[{"process": "mspaint", "snapped": "Left"}]),
        turn("Close it.", direct=["CloseWindow"], settle=1500),
    ], pre=[], cleanup=[]),
    scenario("p3.native-two-windows", "Two windows, follow-up to the one opened first", ["native"], [
        turn("Open Character Map.", settle=2000, windows=[{"process": "charmap"}]),
        turn("Open Paint.", settle=2500, windows=[{"process": "mspaint"}]),
        turn("Close the first one I opened.", settle=2200, windows=[{"process": "charmap", "exists": False}, {"process": "mspaint"}]),
        turn("Close it.", settle=2200, windows=[{"process": "mspaint", "exists": False}]),
    ], pre=[], cleanup=[]),
    scenario("p3.native-stale-window", "Reference to a window that was already closed", ["native", "stale"], [
        turn("Open Character Map.", settle=2000, windows=[{"process": "charmap"}]),
        turn("Close it.", settle=2200, windows=[{"process": "charmap", "exists": False}]),
        turn("Maximize it.", outcome="Clarify", accepted=["Clarify", "Complete", "Failed"],
             before=FOCUS_CHROME),
    ], pre=page_pre(), setup=page_setup("/docs"), cleanup=[]),
    # ---- Cross-surface ---------------------------------------------------------------------
    scenario("p3.cross-browser-then-native", "Earlier browser page, foreground native app: 'it' is the native window", ["cross"], [
        turn("Open the API reference.", url="docs/api"),
        turn("Open Character Map.", settle=2000, windows=[{"process": "charmap"}]),
        turn("Close it.", settle=2200, windows=[{"process": "charmap", "exists": False}, {"process": "chrome"}]),
    ], setup=page_setup("/docs"), pre=page_pre()),
    scenario("p3.cross-native-then-browser", "Earlier native window, foreground browser: 'it' stays with the browser", ["cross"], [
        turn("Open Character Map.", settle=2000, windows=[{"process": "charmap"}]),
        turn("Open the API reference.", url="docs/api", before=FOCUS_CHROME, accepted=["Complete", "Clarify"]),
        turn("Snap it to the right.", windows=[{"process": "chrome", "snapped": "Right"}]),
        turn("Close Character Map.", settle=2200, windows=[{"process": "charmap", "exists": False}]),
    ], setup=page_setup("/docs"), pre=page_pre()),
    # ---- Fresh, unseen (real sites, not IMDb / Rust / lo-fi / Sharp Objects) ---------------------
    scenario("p3.fresh-wiki-other", "Two mountains, then the other one", ["fresh", "browser", "other"], [
        turn("Open the Wikipedia page for Mount Kilimanjaro.", url="Kilimanjaro"),
        turn("Open the Wikipedia page for Mount Elbrus.", url="Elbrus"),
        turn("Now open the other one.", url="Kilimanjaro"),
    ]),
    scenario("p3.fresh-wiki-property", "Two Mercury pages, follow-up by property", ["fresh", "browser", "property"], [
        turn("Open the Wikipedia page for Mercury the planet.", url="Mercury_(planet)"),
        turn("Open the Wikipedia page for Mercury the chemical element.", url="Mercury_(element)"),
        turn("Go back to the planet one.", url="Mercury_(planet)"),
    ]),
    scenario("p3.fresh-mdn-return", "Bring an earlier docs page back", ["fresh", "browser", "return"], [
        turn("Open the MDN documentation for Array map.", origin="developer.mozilla.org"),
        turn("Open the Wikipedia page for the Eiffel Tower.", url="Eiffel_Tower"),
        turn("Bring that MDN page back up.", origin="developer.mozilla.org", new_tabs={"max": 1}),
    ]),
    scenario("p3.fresh-python-again", "Open a docs page, open another, open the first again", ["fresh", "browser", "item"], [
        turn("Open the Python documentation for the pathlib module.", url="pathlib"),
        turn("Open the Python documentation for the itertools module.", url="itertools"),
        turn("Open the pathlib one again.", url="pathlib"),
    ]),
]

path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "pass3.json")
json.dump(out, open(path, "w", encoding="utf-8"), indent=1)
print(f"{len(out)} scenarios -> {path}")
