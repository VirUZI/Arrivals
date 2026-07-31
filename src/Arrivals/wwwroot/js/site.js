(() => {
    "use strict";

    const form = document.querySelector("[data-arrivals-filters]");
    let results = document.querySelector("#arrivals-results");
    let range = document.querySelector("#arrivals-range");

    if (!form || !results || !range) {
        return;
    }

    const progress = form.querySelector("[data-filter-progress]");
    const refreshButton = form.querySelector("button[name='ForceRefresh']");
    const parser = new DOMParser();
    const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
    let activeRequest;
    let inputTimer;

    const animateRows = (container) => {
        if (reducedMotion.matches) {
            return;
        }

        const rows = [...container.querySelectorAll(".arrival-row")];
        window.requestAnimationFrame(() => {
            window.requestAnimationFrame(() => {
                rows.forEach((row, index) => {
                    row.style.setProperty("--flap-delay", `${(index % 10) * 22}ms`);
                    row.classList.add("is-flipping");
                });
            });
        });

        window.setTimeout(() => {
            rows.forEach((row) => row.classList.remove("is-flipping"));
        }, 860);
    };

    const setLoading = (loading) => {
        results.setAttribute("aria-busy", loading ? "true" : "false");
        if (refreshButton) {
            refreshButton.disabled = loading;
        }
        if (progress) {
            if (loading) {
                progress.textContent = "Updating…";
            } else if (progress.textContent === "Updating…") {
                progress.textContent = "";
            }
        }
    };

    const buildUrl = (forceRefresh) => {
        const url = new URL(form.action || window.location.href, window.location.href);
        const parameters = new URLSearchParams(new FormData(form));
        parameters.delete("__Invariant");
        form.querySelectorAll("input[type='checkbox'][name]").forEach((checkbox) => {
            parameters.delete(checkbox.name);
            parameters.set(checkbox.name, checkbox.checked ? "true" : "false");
        });
        url.search = parameters.toString();
        url.searchParams.delete("ForceRefresh");
        if (forceRefresh) {
            url.searchParams.set("ForceRefresh", "true");
        }
        return url;
    };

    const updateListing = async (forceRefresh = false) => {
        if (!form.checkValidity()) {
            return;
        }

        activeRequest?.abort();
        const request = new AbortController();
        activeRequest = request;
        const requestUrl = buildUrl(forceRefresh);
        setLoading(true);

        try {
            const response = await fetch(requestUrl, {
                signal: request.signal,
                headers: { "X-Requested-With": "fetch" }
            });
            if (!response.ok) {
                throw new Error(`Listing update failed with status ${response.status}.`);
            }

            const documentFragment = parser.parseFromString(await response.text(), "text/html");
            const nextResults = documentFragment.querySelector("#arrivals-results");
            const nextRange = documentFragment.querySelector("#arrivals-range");
            if (!nextResults || !nextRange) {
                throw new Error("Listing update returned an incomplete page.");
            }

            results.replaceWith(nextResults);
            range.replaceWith(nextRange);
            results = nextResults;
            range = nextRange;

            const displayUrl = new URL(requestUrl);
            displayUrl.searchParams.delete("ForceRefresh");
            window.history.replaceState({}, "", displayUrl);
            animateRows(results);
        } catch (error) {
            if (error.name !== "AbortError") {
                if (progress) {
                    progress.textContent = "Update failed";
                }
                console.error(error);
            }
        } finally {
            if (activeRequest === request) {
                activeRequest = undefined;
                setLoading(false);
            }
        }
    };

    form.addEventListener("change", (event) => {
        if (event.target.matches("select, input[type='checkbox']")) {
            window.clearTimeout(inputTimer);
            updateListing();
        }
    });

    form.addEventListener("input", (event) => {
        if (!event.target.matches("input[type='number']")) {
            return;
        }
        window.clearTimeout(inputTimer);
        inputTimer = window.setTimeout(() => updateListing(), 320);
    });

    form.addEventListener("submit", (event) => {
        event.preventDefault();
        window.clearTimeout(inputTimer);
        updateListing(event.submitter?.name === "ForceRefresh");
    });

    animateRows(results);
})();
