(() => {
    const telegramBot = "RoamiSIMBot";
    const mobile = /Android|iPhone|iPad|iPod/i.test(navigator.userAgent)
        || (navigator.platform === "MacIntel" && navigator.maxTouchPoints > 1);
    if (!mobile) return;

    document.addEventListener("click", event => {
        const link = event.target.closest(`a[href^="https://t.me/${telegramBot}"]`);
        if (!link) return;

        const webUrl = new URL(link.href);
        const start = webUrl.searchParams.get("start");
        const validStart = start && /^[A-Za-z0-9_-]{1,64}$/.test(start) ? start : "landing";
        const appUrl = `tg://resolve?domain=${telegramBot}&start=${encodeURIComponent(validStart)}`;
        event.preventDefault();

        let fallback = window.setTimeout(() => {
            if (!document.hidden) window.location.assign(webUrl.href);
        }, 1800);
        const stopFallback = () => {
            if (!document.hidden) return;
            window.clearTimeout(fallback);
            document.removeEventListener("visibilitychange", stopFallback);
        };
        document.addEventListener("visibilitychange", stopFallback);
        window.location.assign(appUrl);
    });
})();
