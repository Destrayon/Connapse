window.connapse = window.connapse || {};

// Keeps keyboard focus inside a modal dialog while it is open and hands it back when it closes.
// Blazor renders these dialogs as ordinary markup, so nothing else stops Tab from reaching the
// page behind them.
(function () {
    const held = new Map();
    const focusable = 'a[href], button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), [tabindex]:not([tabindex="-1"])';

    window.connapse.holdFocus = function (id) {
        const dialog = document.getElementById(id);
        if (!dialog || held.has(id)) return;

        const returnTo = document.activeElement;
        const onKey = function (e) {
            if (e.key === "Escape") {
                const cancel = dialog.querySelector("[data-dialog-cancel]");
                if (cancel) { e.preventDefault(); cancel.click(); }
                return;
            }
            if (e.key !== "Tab") return;

            const items = Array.from(dialog.querySelectorAll(focusable)).filter(el => el.offsetParent !== null);
            if (items.length === 0) return;
            const first = items[0], last = items[items.length - 1];
            if (e.shiftKey && document.activeElement === first) { e.preventDefault(); last.focus(); }
            else if (!e.shiftKey && document.activeElement === last) { e.preventDefault(); first.focus(); }
            else if (!dialog.contains(document.activeElement)) { e.preventDefault(); first.focus(); }
        };

        document.addEventListener("keydown", onKey, true);
        held.set(id, { onKey, returnTo });

        const start = dialog.querySelector("[autofocus]") || dialog.querySelector("select, input, textarea") || dialog.querySelector(focusable);
        if (start) start.focus();
    };

    window.connapse.releaseFocus = function (id) {
        const entry = held.get(id);
        if (!entry) return;
        held.delete(id);
        document.removeEventListener("keydown", entry.onKey, true);
        if (entry.returnTo && document.body.contains(entry.returnTo)) entry.returnTo.focus();
    };
})();
