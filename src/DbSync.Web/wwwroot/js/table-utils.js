// table-utils.js - Sortable columns and search filter for all tables
(function () {
    'use strict';

    // ---- SORTING ----
    function initSortable(table) {
        var headers = table.querySelectorAll('thead th');
        headers.forEach(function (th, index) {
            if (th.hasAttribute('data-no-sort')) return;

            th.style.cursor = 'pointer';
            th.style.userSelect = 'none';
            th.classList.add('sortable-th');

            var indicator = document.createElement('span');
            indicator.className = 'sort-indicator ms-1';
            indicator.innerHTML = '\u2195';
            th.appendChild(indicator);

            th.addEventListener('click', function () {
                sortTable(table, index, th);
            });
        });
    }

    function sortTable(table, colIndex, th) {
        var tbody = table.querySelector('tbody');
        if (!tbody) return;

        var rows = Array.from(tbody.querySelectorAll('tr:not(.no-sort)'));
        var sortType = th.dataset.sortType || 'text';

        var currentDir = th.dataset.sortDir || '';
        var dir = currentDir === 'asc' ? 'desc' : 'asc';

        // Reset all headers in this table
        table.querySelectorAll('thead th').forEach(function (h) {
            h.dataset.sortDir = '';
            var ind = h.querySelector('.sort-indicator');
            if (ind) { ind.innerHTML = '\u2195'; ind.style.opacity = '0.4'; }
        });

        th.dataset.sortDir = dir;
        var indicator = th.querySelector('.sort-indicator');
        if (indicator) {
            indicator.innerHTML = dir === 'asc' ? '\u25B2' : '\u25BC';
            indicator.style.opacity = '1';
        }

        rows.sort(function (a, b) {
            var aCell = a.cells[colIndex];
            var bCell = b.cells[colIndex];
            if (!aCell || !bCell) return 0;

            var aVal = (aCell.dataset.sortValue || aCell.textContent).trim().toLowerCase();
            var bVal = (bCell.dataset.sortValue || bCell.textContent).trim().toLowerCase();

            var result;
            if (sortType === 'number') {
                result = (parseFloat(aVal) || 0) - (parseFloat(bVal) || 0);
            } else if (sortType === 'date') {
                result = parseDate(aVal) - parseDate(bVal);
            } else {
                result = aVal.localeCompare(bVal, 'es');
            }

            return dir === 'desc' ? -result : result;
        });

        rows.forEach(function (row) { tbody.appendChild(row); });
    }

    function parseDate(str) {
        // dd/MM/yyyy HH:mm:ss or dd/MM/yy HH:mm or dd/MM HH:mm
        var match = str.match(/(\d{1,2})\/(\d{1,2})(?:\/(\d{2,4}))?\s*(\d{1,2}):(\d{2})(?::(\d{2}))?/);
        if (!match) return 0;
        var day = parseInt(match[1]);
        var month = parseInt(match[2]) - 1;
        var year = match[3] ? parseInt(match[3]) : new Date().getFullYear();
        if (year < 100) year += 2000;
        var hour = parseInt(match[4]) || 0;
        var min = parseInt(match[5]) || 0;
        var sec = parseInt(match[6]) || 0;
        return new Date(year, month, day, hour, min, sec).getTime();
    }

    // ---- SEARCH ----
    function initSearch(input) {
        var tableId = input.dataset.tableSearch;
        var table = document.getElementById(tableId);
        if (!table) return;

        input.addEventListener('input', function () {
            filterTable(table, this.value, tableId);
        });

        // Also trigger on paste
        input.addEventListener('paste', function () {
            var self = this;
            setTimeout(function () { filterTable(table, self.value, tableId); }, 10);
        });
    }

    function filterTable(table, searchValue, tableId) {
        var term = searchValue.toLowerCase().trim();
        var tbody = table.querySelector('tbody');
        if (!tbody) return;

        var rows = tbody.querySelectorAll('tr');
        var visibleCount = 0;

        rows.forEach(function (row) {
            if (row.classList.contains('no-filter')) { visibleCount++; return; }
            var text = row.textContent.toLowerCase();
            var match = !term || text.indexOf(term) >= 0;
            row.style.display = match ? '' : 'none';
            if (match) visibleCount++;
        });

        // Update counter
        var counter = document.querySelector('[data-search-count="' + tableId + '"]');
        if (counter) {
            counter.textContent = term
                ? visibleCount + ' de ' + rows.length + ' registros'
                : rows.length + ' registros';
        }

        // Dispatch event for other scripts (e.g., checkbox counters)
        table.dispatchEvent(new CustomEvent('table-filtered'));
    }

    // ---- LOADING SPINNERS ----
    // Convention: <button data-loading="Buscando..."> or <button data-loading>
    // Optional: data-overlay on the button shows a full-page overlay with the loading text

    function createOverlay() {
        var overlay = document.getElementById('spinnerOverlay');
        if (!overlay) {
            overlay = document.createElement('div');
            overlay.id = 'spinnerOverlay';
            overlay.className = 'spinner-overlay';
            overlay.innerHTML = '<div class="spinner-border text-primary" style="width:2.5rem;height:2.5rem"></div>' +
                '<div class="spinner-text"></div>';
            document.body.appendChild(overlay);
        }
        return overlay;
    }

    function initLoadingButtons() {
        var lastClickedBtn = null;

        // Wrap button content for visibility toggle
        document.querySelectorAll('[data-loading]').forEach(function (el) {
            if (!el.querySelector('.btn-content')) {
                el.innerHTML = '<span class="btn-content">' + el.innerHTML + '</span>';
            }
        });

        // Track which submit button was clicked
        document.addEventListener('click', function (e) {
            var btn = e.target.closest('button[data-loading]');
            if (btn) lastClickedBtn = btn;
        });

        // Activate spinner on form submit
        document.addEventListener('submit', function (e) {
            var form = e.target;
            var btn = lastClickedBtn && lastClickedBtn.form === form
                ? lastClickedBtn
                : form.querySelector('button[data-loading][type="submit"]');

            if (!btn) return;

            showLoading(btn);
            lastClickedBtn = null;
        });

        // Handle links with data-loading (e.g., "vs Actual")
        document.querySelectorAll('a[data-loading]').forEach(function (link) {
            link.addEventListener('click', function () {
                showLoading(this);
            });
        });
    }

    function showLoading(el) {
        var loadingText = el.dataset.loading || '';

        el.classList.add('is-loading');
        if (el.tagName === 'BUTTON') el.disabled = true;

        if (el.hasAttribute('data-overlay')) {
            var overlay = createOverlay();
            var textEl = overlay.querySelector('.spinner-text');
            if (textEl) textEl.textContent = loadingText;
            overlay.classList.add('active');
        }
    }

    // ---- DIFF COPY BUTTONS ----
    function extractDiffText(table, colIndex) {
        var lines = [];
        table.querySelectorAll('tbody tr').forEach(function (tr) {
            var td = tr.cells[colIndex];
            if (!td || td.classList.contains('diff-imaginary')) return;
            var pre = td.querySelector('pre');
            if (pre) lines.push(pre.textContent);
        });
        return lines.join('\n');
    }

    function createCopyBtn(label, title, onClick) {
        var btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'btn btn-sm btn-outline-secondary diff-copy-btn';
        btn.title = title;
        btn.innerHTML = '<i class="bi bi-clipboard"></i> ' + label;
        btn.addEventListener('click', function () {
            var text = onClick();
            navigator.clipboard.writeText(text).then(function () {
                var icon = btn.querySelector('i');
                icon.className = 'bi bi-clipboard-check';
                btn.classList.replace('btn-outline-secondary', 'btn-success');
                setTimeout(function () {
                    icon.className = 'bi bi-clipboard';
                    btn.classList.replace('btn-success', 'btn-outline-secondary');
                }, 2000);
            });
        });
        return btn;
    }

    function addDiffCopyButtons(diffTable) {
        if (diffTable.dataset.copyInit) return;
        if (diffTable.closest('[data-no-copy-btns]')) return;
        diffTable.dataset.copyInit = 'true';

        var headers = diffTable.querySelectorAll('thead th');
        if (headers.length < 4) return;

        var labelLeft = headers[1].textContent.trim() || 'Origen';
        var labelRight = headers[3].textContent.trim() || 'Destino';

        var toolbar = document.createElement('div');
        toolbar.className = 'd-flex gap-1 justify-content-end mb-1';

        toolbar.appendChild(createCopyBtn(labelLeft, 'Copiar ' + labelLeft, function () {
            return extractDiffText(diffTable, 1);
        }));
        toolbar.appendChild(createCopyBtn(labelRight, 'Copiar ' + labelRight, function () {
            return extractDiffText(diffTable, 3);
        }));

        diffTable.parentNode.insertBefore(toolbar, diffTable);
    }

    function scanDiffTables(root) {
        (root || document).querySelectorAll('.diff-table').forEach(addDiffCopyButtons);
    }

    // Observe DOM for dynamically injected diff tables (Compare, CrossCompare pages)
    function initDiffObserver() {
        var containers = document.querySelectorAll('.diff-container');
        if (!containers.length) return;

        var observer = new MutationObserver(function (mutations) {
            mutations.forEach(function (m) {
                m.addedNodes.forEach(function (node) {
                    if (node.nodeType !== 1) return;
                    if (node.classList && node.classList.contains('diff-table')) {
                        addDiffCopyButtons(node);
                    } else if (node.querySelector) {
                        node.querySelectorAll('.diff-table').forEach(addDiffCopyButtons);
                    }
                });
            });
        });

        containers.forEach(function (c) {
            observer.observe(c, { childList: true, subtree: true });
        });
    }

    // ---- INIT ----
    document.addEventListener('DOMContentLoaded', function () {
        document.querySelectorAll('.sortable-table').forEach(initSortable);
        document.querySelectorAll('[data-table-search]').forEach(initSearch);
        initLoadingButtons();
        scanDiffTables();
        initDiffObserver();
    });
})();
