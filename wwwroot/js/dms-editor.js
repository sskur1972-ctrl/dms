/*
 * dms-editor.js - the in-application document editor.
 *
 * WHY THIS IS HAND-WRITTEN RATHER THAN A LIBRARY
 * ----------------------------------------------
 *  1. The Content-Security-Policy is `script-src 'self'`. Nothing loads from a
 *     CDN, and the popular editors either want one or ship a build that needs
 *     eval for their template compilers.
 *
 *  2. This is a validated GxP system. Every third-party component has to be
 *     qualified and re-qualified on each vendor update. A rich-text editor is a
 *     large, fast-moving dependency to carry through that process.
 *
 *  3. The document that matters is the .docx the server writes from what this
 *     produces. An editor whose model is richer than the OOXML writer can
 *     express would silently lose formatting at save time - the worst kind of
 *     failure, because it looks like it worked.
 *
 * So the feature set is deliberately the intersection of what a procedure author
 * needs and what the writer can put in a Word file faithfully. Every control on
 * the ribbon does something real; there are no placeholders.
 *
 * WHY document.execCommand
 * ------------------------
 * Deprecated, and implemented everywhere. It also drives the browser's OWN undo
 * stack. A hand-rolled command model would have to reimplement undo, redo and
 * their interaction with the IME and the user's Ctrl+Z reflex, and getting that
 * subtly wrong loses an author's work. Where execCommand cannot express
 * something - line spacing, borders, table geometry - this file manipulates the
 * DOM directly and pushes the change through the same dirty/save path.
 *
 * WHAT IS NOT HERE, ON PURPOSE
 * ----------------------------
 * Nothing is written to localStorage or sessionStorage. A controlled document
 * must not have a copy in a browser profile that outlives the session, sits
 * outside the audit trail, and cannot be withdrawn when the document is
 * obsoleted. Unsaved work is protected on the server instead, by an autosave
 * buffer that lives with the record.
 */
(function () {
    'use strict';

    var root = document.querySelector('[data-dms-editor]');
    if (!root) return;

    // =====================================================================
    // Configuration and state
    // =====================================================================

    var config = {
        saveUrl: root.getAttribute('data-save-url') || '',
        autosaveUrl: root.getAttribute('data-autosave-url') || '',
        documentId: root.getAttribute('data-document-id') || '',
        versionId: root.getAttribute('data-version-id') || '',
        templateId: root.getAttribute('data-template-id') || '',
        autosaveSeconds: parseInt(root.getAttribute('data-autosave-seconds') || '0', 10),
        maxImageBytes: parseInt(root.getAttribute('data-max-image-bytes') || '2097152', 10),
        readOnly: root.getAttribute('data-read-only') === '1',
        headerFooterEnabled: root.getAttribute('data-header-footer') !== '0'
    };

    var surface = root.querySelector('[data-dms-editor-surface]');
    if (!surface) return;

    var pagesBox = root.querySelector('[data-dms-pages]');
    var canvas = root.querySelector('[data-dms-canvas]');
    var statusBox = root.querySelector('[data-dms-editor-status]');
    var countBox = root.querySelector('[data-dms-editor-count]');
    var pageCountBox = root.querySelector('[data-dms-page-count]');
    var rowVersionField = root.querySelector('[data-dms-editor-rowversion]');
    var imageInput = root.querySelector('[data-dms-editor-image-input]');
    var findPanel = root.querySelector('[data-dms-editor-find]');

    /** Page geometry. Everything in the payload is in twips; see DocumentPageSetupDto. */
    var pageSetup = readPageSetup();

    var dirty = false;
    var saving = false;
    var autosaveTimer = null;
    var lastAutosavedHtml = '';
    var zoom = 1;
    var formatPainter = null;

    function readPageSetup() {
        var raw = root.getAttribute('data-page-setup');
        var fallback = {
            pageSize: 'A4', orientation: 'portrait',
            marginTopTwips: 1440, marginRightTwips: 1440,
            marginBottomTwips: 1440, marginLeftTwips: 1440,
            headerDistanceTwips: 708, footerDistanceTwips: 708,
            gutterTwips: 0, columns: 1,
            headerHtml: null, footerHtml: null,
            firstHeaderHtml: null, firstFooterHtml: null,
            evenHeaderHtml: null, evenFooterHtml: null,
            differentFirstPage: false, differentOddEven: false, showPageNumbers: true,
            pageNumberFormat: 'PAGE_X_OF_Y', pageNumberPosition: 'FOOTER_CENTER',
            styleSet: 'DEFAULT', autoNumberHeadings: false
        };

        if (!raw) return fallback;

        try {
            var parsed = JSON.parse(raw);
            for (var key in fallback) {
                if (Object.prototype.hasOwnProperty.call(fallback, key) && parsed[key] === undefined) {
                    parsed[key] = fallback[key];
                }
            }
            return parsed;
        } catch (e) {
            return fallback;
        }
    }

    // ---------------------------------------------------------------------
    // Geometry
    //
    // A twip is 1/1440 inch and a CSS pixel is 1/96 inch, so a twip is exactly
    // 1/15 of a pixel. Doing the arithmetic in one place, from the same numbers
    // the .docx writer uses, is what keeps the page on screen the same size as
    // the page in the file.
    // ---------------------------------------------------------------------

    var PAPER = {
        A4: [11906, 16838], A3: [16838, 23811],
        LETTER: [12240, 15840], LEGAL: [12240, 20160]
    };

    /** Visual gap between sheets, in CSS pixels. Not part of the document. */
    var PAGE_GAP = 26;

    /** How many pages were last drawn - what "next page" and page labels count against. */
    var pageCountCache = 1;

    function twipsToPx(twips) { return twips / 15; }

    /**
     * The measured height of the running header and footer bands.
     *
     * Not a constant, because a header can contain a picture: a site logo is the
     * commonest thing anybody puts in one, and a logo is taller than a line of
     * text. Word grows the text area away from a header that is taller than the
     * margin it sits in rather than letting the two overlap, and so does this -
     * see the clamp on marginTop and marginBottom below. Measured in
     * measureRunning() once per reflow.
     */
    var runningHeights = { header: 0, footer: 0 };

    /** Space kept between a running band and the body text, in CSS pixels. */
    var RUNNING_CLEARANCE = 6;

    function geometry() {
        var paper = PAPER[(pageSetup.pageSize || 'A4').toUpperCase()] || PAPER.A4;
        var landscape = (pageSetup.orientation || 'portrait').toLowerCase() === 'landscape';

        var widthTwips = landscape ? paper[1] : paper[0];
        var heightTwips = landscape ? paper[0] : paper[1];

        var pageWidth = twipsToPx(widthTwips);
        var pageHeight = twipsToPx(heightTwips);

        var headerTop = twipsToPx(pageSetup.headerDistanceTwips);
        var footerBottom = twipsToPx(pageSetup.footerDistanceTwips);

        // The margin the author set, or as much as the running band needs -
        // whichever is larger. Capped so that an accidentally enormous picture
        // in a header cannot squeeze the text area out of existence.
        var maxBand = pageHeight * 0.4;

        var marginTop = Math.min(maxBand, Math.max(twipsToPx(pageSetup.marginTopTwips),
            runningHeights.header > 0 ? headerTop + runningHeights.header + RUNNING_CLEARANCE : 0));

        var marginBottom = Math.min(maxBand, Math.max(twipsToPx(pageSetup.marginBottomTwips),
            runningHeights.footer > 0 ? footerBottom + runningHeights.footer + RUNNING_CLEARANCE : 0));

        var marginLeft = twipsToPx(pageSetup.marginLeftTwips + (pageSetup.gutterTwips || 0));
        var marginRight = twipsToPx(pageSetup.marginRightTwips);

        var contentHeight = Math.max(80, pageHeight - marginTop - marginBottom);

        return {
            pageWidth: pageWidth,
            pageHeight: pageHeight,
            marginTop: marginTop,
            marginBottom: marginBottom,
            marginLeft: marginLeft,
            marginRight: marginRight,
            contentWidth: Math.max(80, pageWidth - marginLeft - marginRight),
            contentHeight: contentHeight,

            // Distance, in the surface's own coordinates, from the top of one
            // page's text area to the top of the next. The surface is one
            // continuous flow, so the space taken by the bottom margin, the gap
            // and the next top margin has to be skipped by a spacer.
            stride: contentHeight + marginBottom + PAGE_GAP + marginTop,
            headerTop: headerTop,
            footerBottom: footerBottom
        };
    }

    // =====================================================================
    // Status
    // =====================================================================

    function setStatus(text, kind) {
        if (!statusBox) return;
        statusBox.textContent = text;
        statusBox.className = 'dms-editor-status' + (kind ? ' is-' + kind : '');
    }

    function markDirty() {
        if (config.readOnly) return;
        if (!dirty) { dirty = true; setStatus('Unsaved changes', 'warn'); }
        scheduleReflow();
    }

    /**
     * What a read-only session says when a control is used anyway.
     *
     * <para>
     * Every editing function used to begin "if (config.readOnly) return;" and
     * say nothing. A reviewer opening a document they may not edit therefore
     * met a full ribbon in which every single button and dropdown did
     * absolutely nothing, with no explanation - indistinguishable from a broken
     * editor, and the likeliest explanation for "the combo boxes are not
     * working". The controls are now disabled outright (see disableEditingControls)
     * and this is the belt to that braces for anything reached by a keyboard
     * shortcut.
     * </para>
     */
    function readOnlyRefusal() {
        setStatus('Read-only — your access rights do not permit editing this document.', 'warn');
    }

    // =====================================================================
    // Pagination
    //
    // The editing surface is ONE contenteditable flow. Splitting the document
    // into a separate editable element per page would give crisper pages and
    // would also break every selection, every paste and every undo that crossed
    // a page boundary - the operations authors use constantly. So the pages are
    // drawn behind a single flow, and the flow is pushed past the gaps with
    // spacers that are not part of the document.
    //
    // Spacers are marked, are contenteditable=false, and are stripped before the
    // body is sent to the server. Nothing about pagination is ever stored.
    // =====================================================================

    var SPACER = 'data-dms-spacer';
    var reflowTimer = null;
    var reflowing = false;

    function scheduleReflow() {
        if (reflowTimer) clearTimeout(reflowTimer);
        reflowTimer = setTimeout(reflow, 120);
    }

    function clearSpacers() {
        var spacers = surface.querySelectorAll('[' + SPACER + ']');
        for (var i = spacers.length - 1; i >= 0; i--) spacers[i].parentNode.removeChild(spacers[i]);
    }

    function makeSpacer(height, tag) {
        var spacer = document.createElement(tag || 'div');
        spacer.setAttribute(SPACER, '1');
        spacer.setAttribute('contenteditable', 'false');
        spacer.className = 'dms-page-spacer';
        spacer.style.height = Math.max(0, height) + 'px';
        return spacer;
    }

    /**
     * Applies the page geometry, breaks the flow across pages, and redraws the
     * sheets behind it.
     *
     * Guarded against re-entry: the DOM changes it makes fire `input`, which
     * would schedule another reflow, which would make more changes.
     */
    function reflow() {
        if (reflowing) return;
        reflowing = true;

        try {
            var g = geometry();
            applyGeometry(g);

            // Measure the running bands against this geometry. If a picture in
            // the header or footer makes a band taller than the margin it lives
            // in, the text area has to move before anything is paginated -
            // otherwise the first page is laid out against a margin the header
            // is already sitting in, which is the overlap the customer reported.
            var measured = measureRunning(g);

            if (Math.abs(measured.header - runningHeights.header) > 1 ||
                Math.abs(measured.footer - runningHeights.footer) > 1) {
                runningHeights = measured;
                g = geometry();
                applyGeometry(g);
            }

            clearSpacers();

            // Generated content is settled BEFORE the flow is broken, because
            // section numbers and index entries take up room and therefore
            // change where the page breaks fall.
            if (pageSetup.autoNumberHeadings) applyAutoNumbers();
            var structureChanged = rebuildIndex(false);

            paginate(g);

            // Page numbers can only be known once the flow is broken. Filling
            // them in can change the index's height, so the flow is broken once
            // more when it does - once, not until it settles: an index whose own
            // growth moved a heading onto the next page would otherwise chase
            // itself.
            if (refreshIndexPageNumbers(g) || structureChanged) {
                clearSpacers();
                paginate(g);
                refreshIndexPageNumbers(g);
            }

            drawPages(g);
            rebuildNavigation();
            positionImageHandles();
        } finally {
            reflowing = false;
        }
    }

    /**
     * How tall the header and footer bands actually are, measured by laying one
     * of each out off-screen at the real content width. An estimate from a line
     * count was accurate enough while a running band could only hold text; it is
     * not once it can hold a picture.
     */
    function measureRunning(g) {
        if (!pagesBox) return { header: 0, footer: 0, headerContent: 0, footerContent: 0 };

        var probe = document.createElement('div');
        probe.className = 'dms-page-furniture dms-page-probe';
        probe.setAttribute('aria-hidden', 'true');

        // The tallest variant decides the margin, so that turning a page does
        // not move the text area: a logo on the first page only would otherwise
        // give page 1 a different text height from page 2.
        var headerContent = 0;
        var footerContent = 0;
        var built = [];

        for (var i = 0; i < RUNNING_VARIANTS.length; i++) {
            var variant = RUNNING_VARIANTS[i];
            if (!variantIsUsed(variant)) continue;

            var header = buildRunningContent('header', variant);
            var footer = buildRunningContent('footer', variant);

            if (header) { header.style.top = '0px'; probe.appendChild(header); built.push(['header', header]); }
            if (footer) { footer.style.top = '0px'; probe.appendChild(footer); built.push(['footer', footer]); }
        }

        if (built.length > 0) {
            pagesBox.appendChild(probe);

            for (var m = 0; m < built.length; m++) {
                var height = built[m][1].offsetHeight;
                if (built[m][0] === 'header') headerContent = Math.max(headerContent, height);
                else footerContent = Math.max(footerContent, height);
            }

            pagesBox.removeChild(probe);
        }

        // An empty band still needs somewhere to click, and the live editable
        // band has a minimum height, so the reserved space has to allow for it.
        if (hfMode) {
            if (usesHeaderBand()) headerContent = Math.max(headerContent, LIVE_BAND_MIN_HEIGHT);
            if (usesFooterBand()) footerContent = Math.max(footerContent, LIVE_BAND_MIN_HEIGHT);
        }

        return {
            headerContent: headerContent,
            footerContent: footerContent,
            header: headerContent + (numberInHeader() ? PAGE_NUMBER_HEIGHT : 0),
            footer: footerContent + (numberInFooter() ? PAGE_NUMBER_HEIGHT : 0)
        };
    }

    function applyGeometry(g) {
        surface.style.width = g.pageWidth + 'px';
        surface.style.paddingTop = g.marginTop + 'px';
        surface.style.paddingRight = g.marginRight + 'px';
        surface.style.paddingBottom = g.marginBottom + 'px';
        surface.style.paddingLeft = g.marginLeft + 'px';
        surface.style.minHeight = g.pageHeight + 'px';

        surface.style.columnCount = (pageSetup.columns || 1) > 1 ? String(pageSetup.columns) : '';
        surface.style.columnGap = (pageSetup.columns || 1) > 1 ? '28px' : '';

        if (pagesBox) pagesBox.style.width = g.pageWidth + 'px';
    }

    /**
     * The distance from the top of the text area to the top of this element, in
     * the surface's own coordinates.
     *
     * Walks the offsetParent chain rather than reading offsetTop once, because
     * offsetTop is measured against the nearest POSITIONED ancestor for most
     * elements but against the table for a row. Reading it directly gives an
     * answer that is right for a paragraph and wrong for a table row - which is
     * the sort of difference that only shows up on the page that has a table on
     * it.
     */
    function topInSurface(element) {
        var y = 0;
        var node = element;

        while (node && node !== surface) {
            y += node.offsetTop;
            node = node.offsetParent;
        }

        // The surface's padding-top IS the top margin, so offsets measured from
        // its padding box already include it.
        return y - geometryCache.marginTop;
    }

    var geometryCache = { marginTop: 0 };

    /**
     * Breaks the flow across pages.
     *
     * WHY THIS IS RECURSIVE
     * ---------------------
     * The first version walked only the surface's direct children. That is fine
     * for a document typed in the editor, where every paragraph is a top-level
     * block, and it fails completely for pasted content: a paste from Word or a
     * PDF arrives as ONE wrapper div containing the whole document. The wrapper
     * was taller than a page, was not a table, and so was skipped - producing a
     * document with no page breaks at all, whose content ran straight through
     * every footer, gap and header. That is the defect the customer reported,
     * and it is reproduced in the browser test.
     *
     * So the rule is now general: anything that does not fit is either PUSHED to
     * the next page, or - when it is too tall to fit on any page - opened up and
     * its own children paginated. A table is opened at its rows; anything else
     * at its block children.
     */
    function paginate(g) {
        if ((pageSetup.columns || 1) > 1) return;   // columns and pagination do not compose

        geometryCache = g;
        paginateContainer(surface, g, 0, { inserted: 0 });
    }

    /** Elements that can be broken open and paginated from the inside. */
    function isContainer(element) {
        return /^(DIV|SECTION|ARTICLE|BLOCKQUOTE|UL|OL|LI|TBODY|THEAD|TFOOT|FIGURE)$/.test(element.tagName);
    }

    function paginateContainer(container, g, depth, budget) {
        if (depth > 6) return;

        var children = container.children;

        for (var i = 0; i < children.length; i++) {
            if (budget.inserted > 600) return;   // pathological document; stop rather than hang

            var child = children[i];
            if (child.hasAttribute(SPACER)) continue;
            if (child.nodeType !== 1) continue;

            var height = child.offsetHeight;
            if (height <= 0) continue;

            var top = topInSurface(child);
            var page = Math.floor(top / g.stride);
            var offsetInPage = top - (page * g.stride);

            if (offsetInPage + height <= g.contentHeight) continue;   // it fits where it is

            if (height > g.contentHeight) {
                // Too tall for any page. Open it up rather than leave it to run
                // through every boundary.
                if (child.tagName === 'TABLE') {
                    paginateTable(child, g, budget);
                } else if (isContainer(child) && child.children.length > 0) {
                    paginateContainer(child, g, depth + 1, budget);
                } else if (offsetInPage > 4) {
                    // Atomic and taller than a page - an oversized picture, say.
                    // It cannot be made to fit, but starting it at the top of a
                    // page gives it the most room and keeps the break tidy.
                    pushToNextPage(container, child, top, g, budget);
                }

                continue;
            }

            pushToNextPage(container, child, top, g, budget);

            // Step over the spacer just inserted, or the loop re-measures the
            // same child and inserts another.
            i++;
        }
    }

    function pushToNextPage(container, child, top, g, budget) {
        var page = Math.floor(top / g.stride);
        var needed = ((page + 1) * g.stride) - top;

        if (needed <= 0 || needed > g.stride) return;

        var tag = container.tagName === 'TABLE' || /^(TBODY|THEAD|TFOOT)$/.test(container.tagName)
            ? 'tr'
            : 'div';

        var spacer = makeSpacer(needed, tag);

        if (tag === 'tr') {
            var cell = document.createElement('td');
            cell.setAttribute('colspan', String(columnCountOf(container)));
            cell.className = 'dms-page-spacer-cell';
            spacer.appendChild(cell);
        }

        container.insertBefore(spacer, child);
        budget.inserted++;
    }

    function columnCountOf(node) {
        var table = node.tagName === 'TABLE' ? node : node.closest('table');
        if (!table) return 1;

        var columns = 1;
        for (var r = 0; r < table.rows.length; r++) columns = Math.max(columns, table.rows[r].cells.length);
        return columns;
    }

    /**
     * Breaks one table between its rows. The spacer is a real row whose single
     * cell has no border and no background, so the gap between sheets shows
     * through cleanly rather than the table's own rules being drawn across it.
     */
    function paginateTable(table, g, budget) {
        var sections = table.tBodies.length > 0
            ? Array.prototype.slice.call(table.tBodies)
            : [table];

        if (table.tHead) sections.unshift(table.tHead);

        for (var s = 0; s < sections.length; s++) paginateContainer(sections[s], g, 5, budget);
    }

    /** Draws the sheets, and the running header and footer on each. */
    function drawPages(g) {
        if (!pagesBox) return;

        var flowHeight = surface.scrollHeight - g.marginTop - g.marginBottom;
        var pageCount = Math.max(1, Math.ceil((flowHeight + 1) / g.stride));

        // A trailing spacer can leave an empty final sheet; do not draw one.
        if (pageCount > 1 && flowHeight <= (pageCount - 1) * g.stride) pageCount--;

        // Only what this function drew last time: the live band layer has to
        // survive, or editing a header would lose the caret on every keystroke.
        var stale = pagesBox.querySelectorAll('.dms-page, .dms-page-furniture, .dms-page-probe');
        for (var s = stale.length - 1; s >= 0; s--) stale[s].parentNode.removeChild(stale[s]);

        pageCountCache = pageCount;
        pagesBox.style.height = ((pageCount * g.pageHeight) + ((pageCount - 1) * PAGE_GAP)) + 'px';

        for (var i = 0; i < pageCount; i++) {
            var sheet = document.createElement('div');
            sheet.className = 'dms-page';
            sheet.style.top = (i * (g.pageHeight + PAGE_GAP)) + 'px';
            sheet.style.height = g.pageHeight + 'px';

            pagesBox.appendChild(sheet);
        }

        // ---- page furniture, drawn ABOVE the text ----------------------------
        //
        // The margins and the gap between sheets are drawn as opaque strips over
        // the editable flow. Pagination should keep body text out of them, but
        // "should" is not a guarantee an author can see: the customer's
        // screenshot was text running through a footer, over a gap and into the
        // next page's header, and a page view whose margins can be trespassed on
        // does not look like a page. Masking makes the paper always look like
        // paper, and the header and footer sit on top of the mask where they
        // belong.
        var furniture = document.createElement('div');
        furniture.className = 'dms-page-furniture';
        furniture.setAttribute('aria-hidden', 'true');

        for (var f = 0; f < pageCount; f++) {
            var pageTop = f * (g.pageHeight + PAGE_GAP);

            furniture.appendChild(mask(pageTop, g.marginTop, 'is-paper'));
            furniture.appendChild(
                mask(pageTop + g.pageHeight - g.marginBottom, g.marginBottom, 'is-paper'));

            if (f < pageCount - 1) furniture.appendChild(mask(pageTop + g.pageHeight, PAGE_GAP, 'is-gap'));

            drawRunningBands(furniture, f + 1, pageCount, pageTop, g);

            var label = document.createElement('span');
            label.className = 'dms-page-label';
            label.style.top = (pageTop + g.pageHeight + 4) + 'px';
            label.textContent = 'Page ' + (f + 1);
            furniture.appendChild(label);
        }

        pagesBox.appendChild(furniture);
        ensureLiveLayer();          // stays last so it paints above the furniture

        if (pageCountBox) {
            pageCountBox.textContent = pageCount + (pageCount === 1 ? ' page' : ' pages');
        }
    }

    /**
     * Draws one page's header and footer: the content band, then the page
     * number under it.
     *
     * They are separate elements rather than one, because the band being edited
     * is replaced by the live editable element while the page number is not -
     * a page number is generated from the layout, and letting an author edit it
     * would let a controlled copy claim to be page 3 of 11 when it is not.
     */
    function drawRunningBands(furniture, pageNumber, pageCount, pageTop, g) {
        var variant = variantForPage(pageNumber);
        var editingHere = hfMode && hfPage === pageNumber;

        if (usesHeaderBand()) {
            var headerTop = pageTop + g.headerTop;
            var headerHeight = runningHeights.headerContent;

            if (editingHere && hfMode === 'header') {
                placeLiveBand('header', variant, headerTop, g);
            } else {
                var header = buildRunningContent('header', variant);
                if (header) { position(header, headerTop, g); furniture.appendChild(header); }
            }

            var headerNumber = buildPageNumber('header', pageNumber, pageCount);
            if (headerNumber) {
                position(headerNumber, headerTop + headerHeight, g);
                furniture.appendChild(headerNumber);
            }
        }

        if (usesFooterBand()) {
            var total = runningHeights.footerContent + (numberInFooter() ? PAGE_NUMBER_HEIGHT : 0);
            var footerTop = pageTop + g.pageHeight - g.footerBottom - total;

            if (editingHere && hfMode === 'footer') {
                placeLiveBand('footer', variant, footerTop, g);
            } else {
                var footer = buildRunningContent('footer', variant);
                if (footer) { position(footer, footerTop, g); furniture.appendChild(footer); }
            }

            var footerNumber = buildPageNumber('footer', pageNumber, pageCount);
            if (footerNumber) {
                position(footerNumber, footerTop + runningHeights.footerContent, g);
                furniture.appendChild(footerNumber);
            }
        }

        function position(element, top, geo) {
            element.style.top = top + 'px';
            element.style.left = geo.marginLeft + 'px';
            element.style.right = geo.marginRight + 'px';
        }
    }

    /** Moves the live band onto this page without touching its contents. */
    function placeLiveBand(kind, variant, top, g) {
        var band = liveBand(kind);

        band.hidden = false;
        band.style.top = top + 'px';
        band.style.left = g.marginLeft + 'px';
        band.style.right = g.marginRight + 'px';
        band.setAttribute('data-dms-variant', variant);
    }

    /** One opaque strip of page furniture: a margin, or the gap between sheets. */
    function mask(top, height, kind) {
        var strip = document.createElement('div');
        strip.className = 'dms-page-mask ' + kind;
        strip.style.top = top + 'px';
        strip.style.height = Math.max(0, height) + 'px';
        return strip;
    }

    // =====================================================================
    // Running headers and footers
    //
    // WHY THESE ARE EDITED ON THE PAGE AND NOT IN A DIALOG
    // ----------------------------------------------------
    // They used to be edited in a panel, on the reasoning that a single flow
    // with one editable region per page is simpler to keep in step. That was
    // true and it was still the wrong trade: a dialog is not how anybody who
    // uses Word expects to put a logo in a footer, and it made resizing a
    // picture in one genuinely awkward. So the model is now Word's - double
    // click the header or footer area, the body dims, you type on the page -
    // and the "keeping ten copies in step" problem is solved instead of
    // avoided: exactly ONE band is editable at a time (the one on the page you
    // activated), every other page draws a non-editable preview of the same
    // content, and the previews are rebuilt from the authoritative HTML on
    // every reflow.
    //
    // Word has three of each per section, not one: the default, the first
    // page's, and the even (left-hand) pages'. That is modelled here and in the
    // .docx writer, so "different first page" and "different odd & even pages"
    // mean what they mean in Word.
    // =====================================================================

    var RUNNING_VARIANTS = ['default', 'first', 'even'];

    /** Room a page-number line needs, in CSS pixels. One line at the band's size. */
    var PAGE_NUMBER_HEIGHT = 16;

    /** An empty band still has to be big enough to click into and type in. */
    var LIVE_BAND_MIN_HEIGHT = 20;

    /** Which band, if any, is being edited on the page: null | 'header' | 'footer'. */
    var hfMode = null;

    /** The 1-based page whose band is being edited. */
    var hfPage = 1;

    var liveLayer = null;
    var liveBands = { header: null, footer: null };

    function numberInHeader() {
        return !!pageSetup.showPageNumbers &&
            (pageSetup.pageNumberPosition || 'FOOTER_CENTER').toUpperCase().indexOf('HEADER') === 0;
    }

    function numberInFooter() {
        return !!pageSetup.showPageNumbers &&
            (pageSetup.pageNumberPosition || 'FOOTER_CENTER').toUpperCase().indexOf('FOOTER') === 0;
    }

    /** True when this document has a header at all - content, a page number, or one being written. */
    function usesHeaderBand() {
        return !!(pageSetup.headerHtml || pageSetup.firstHeaderHtml || pageSetup.evenHeaderHtml) ||
            numberInHeader() || hfMode === 'header';
    }

    function usesFooterBand() {
        return !!(pageSetup.footerHtml || pageSetup.firstFooterHtml || pageSetup.evenFooterHtml) ||
            numberInFooter() || hfMode === 'footer';
    }

    function variantIsUsed(variant) {
        if (variant === 'first') return !!pageSetup.differentFirstPage;
        if (variant === 'even') return !!pageSetup.differentOddEven;
        return true;
    }

    /**
     * Which of the three variants applies to a page.
     *
     * The same rule Word uses, and the same one the writer's headerReference
     * types express, so the page on screen and the page in the file are driven
     * by one decision rather than two that can disagree.
     */
    function variantForPage(pageNumber) {
        if (pageSetup.differentFirstPage && pageNumber === 1) return 'first';
        if (pageSetup.differentOddEven && pageNumber % 2 === 0) return 'even';
        return 'default';
    }

    /** The pageSetup property holding one band's HTML. */
    function bandProperty(kind, variant) {
        var stem = kind === 'header' ? 'HeaderHtml' : 'FooterHtml';

        if (variant === 'first') return 'first' + stem;
        if (variant === 'even') return 'even' + stem;
        return stem.charAt(0).toLowerCase() + stem.slice(1);
    }

    function bandHtml(kind, variant) {
        return pageSetup[bandProperty(kind, variant)] || '';
    }

    function setBandHtml(kind, variant, html) {
        pageSetup[bandProperty(kind, variant)] = html || null;
    }

    /** A non-editable preview of one band's content, or null when it has none. */
    function buildRunningContent(kind, variant) {
        var html = bandHtml(kind, variant);
        if (!html) return null;

        var band = document.createElement('div');
        band.className = 'dms-page-running is-' + kind;
        band.setAttribute('data-dms-band', kind);
        band.setAttribute('aria-hidden', 'true');

        // Already through the server's whitelist sanitiser, like any other
        // document content.
        band.innerHTML = html;
        return band;
    }

    function buildPageNumber(kind, pageNumber, pageCount) {
        var wants = kind === 'header' ? numberInHeader() : numberInFooter();
        if (!wants) return null;

        var position = (pageSetup.pageNumberPosition || 'FOOTER_CENTER').toUpperCase();

        var number = document.createElement('div');
        number.className = 'dms-page-number';
        number.setAttribute('aria-hidden', 'true');
        number.style.textAlign = position.indexOf('LEFT') > 0 ? 'left'
            : position.indexOf('RIGHT') > 0 ? 'right' : 'center';

        var format = (pageSetup.pageNumberFormat || 'PAGE_X_OF_Y').toUpperCase();
        number.textContent = format === 'PLAIN_X' ? String(pageNumber)
            : format === 'PAGE_X' ? 'Page ' + pageNumber
            : 'Page ' + pageNumber + ' of ' + pageCount;

        return number;
    }

    /**
     * The live editable band.
     *
     * Created once and kept in a layer of its own that redraws never touch,
     * because moving a contenteditable element in the DOM loses the caret - and
     * an editor that drops the caret every time it repaginates is unusable.
     * Redraws only move it, by setting `top`.
     */
    function ensureLiveLayer() {
        if (liveLayer && liveLayer.isConnected) return liveLayer;

        liveLayer = document.createElement('div');
        liveLayer.className = 'dms-page-live';
        pagesBox.appendChild(liveLayer);
        return liveLayer;
    }

    function liveBand(kind) {
        if (liveBands[kind] && liveBands[kind].isConnected) return liveBands[kind];

        var band = document.createElement('div');
        band.className = 'dms-page-running dms-page-live-band is-' + kind;
        band.setAttribute('data-dms-hf', kind);          // the editable-region marker
        band.setAttribute('contenteditable', 'true');
        band.setAttribute('role', 'textbox');
        band.setAttribute('aria-label', 'Running ' + kind);
        band.hidden = true;

        ensureLiveLayer().appendChild(band);
        liveBands[kind] = band;
        return band;
    }

    /**
     * Sizes the editor to the space it actually has, so that the ribbon stays
     * put and only the pages scroll.
     *
     * Measured rather than assumed: the editor sits under a document heading on
     * one page and under an admin heading on another, and a hard-coded
     * `100vh - 210px` would be wrong on both. Recomputed on resize.
     */
    function setShellHeight() {
        if (!canvas) return;

        var top = root.getBoundingClientRect().top;

        // 12px of breathing room under the editor, not 24: the status bar is
        // inside the shell, so the gap below it is dead space on a screen whose
        // whole job is showing a page.
        var available = window.innerHeight - top - 12;

        root.style.height = Math.max(460, Math.round(available)) + 'px';
    }

    function applyZoom() {
        if (!pagesBox || !canvas) return;

        var wrapper = pagesBox.parentNode;
        wrapper.style.transform = zoom === 1 ? '' : 'scale(' + zoom + ')';
        wrapper.style.transformOrigin = 'top center';

        var label = root.querySelector('[data-dms-zoom-label]');
        if (label) label.textContent = Math.round(zoom * 100) + '%';
    }

    // =====================================================================
    // Word count
    // =====================================================================

    var countTimer = null;

    function scheduleCount() {
        if (!countBox) return;
        if (countTimer) clearTimeout(countTimer);
        countTimer = setTimeout(updateCount, 400);
    }

    function updateCount() {
        if (!countBox) return;

        var text = (surface.innerText || '').replace(/\s+/g, ' ').trim();
        var words = text.length === 0 ? 0 : text.split(' ').length;
        var tables = surface.querySelectorAll('table').length;
        var images = surface.querySelectorAll('img').length;

        var parts = [words.toLocaleString() + (words === 1 ? ' word' : ' words')];
        if (tables > 0) parts.push(tables + (tables === 1 ? ' table' : ' tables'));
        if (images > 0) parts.push(images + (images === 1 ? ' figure' : ' figures'));

        countBox.textContent = parts.join(' · ');
    }

    // =====================================================================
    // Selection helpers
    // =====================================================================

    /**
     * The editable region commands act on.
     *
     * Normally the body, but the running header and footer are editable fields
     * too, and an author who has clicked into the footer and then pressed
     * Insert > Picture means the picture to go in the footer. Tracked rather
     * than derived from document.activeElement because clicking a ribbon button
     * moves focus to the button first.
     */
    var activeEditable = surface;

    function editableOf(node) {
        if (!node) return null;
        var element = node.nodeType === 1 ? node : node.parentNode;
        if (!element || !element.closest) return null;
        if (surface.contains(element)) return surface;

        var field = element.closest('[data-dms-hf]');
        return field && root.contains(field) ? field : null;
    }

    function setActiveEditable(element) {
        activeEditable = element && element.isConnected ? element : surface;
    }

    root.addEventListener('focusin', function (event) {
        var editable = editableOf(event.target);
        if (editable) setActiveEditable(editable);
    });

    /**
     * The last range the caret occupied inside an editable region.
     *
     * <para><b>Why this has to exist</b></para>
     * A ribbon &lt;select&gt; takes the focus when it is clicked - a native
     * dropdown cannot be persuaded otherwise. Chrome discards the
     * contenteditable's selection when focus is put back, so by the time the
     * change event fired, the author's selection was gone: execCommand had
     * nothing to act on, the document was marked dirty, and NOTHING happened.
     * That is the reported "the font, size and painter combo boxes do not
     * work" - the commands were running, against an empty selection.
     *
     * <para>
     * Buttons are not affected (Chrome does not focus a button on mousedown)
     * which is exactly why bold worked and the dropdowns did not. Rather than
     * rely on that, every ribbon control now goes through restoreSelection().
     * </para>
     */
    var savedRange = null;

    function rememberSelection() {
        var s = window.getSelection();
        if (!s || s.rangeCount === 0) return;
        if (!editableOf(s.anchorNode)) return;

        savedRange = s.getRangeAt(0).cloneRange();
    }

    /** True when the live selection is inside the editable we are acting on. */
    function selectionIsLive() {
        var s = window.getSelection();
        if (!s || s.rangeCount === 0) return false;
        return !!editableOf(s.anchorNode);
    }

    function restoreSelection(force) {
        if (!force && selectionIsLive()) return true;
        if (!savedRange) return false;

        // A range whose nodes have been replaced (a reflow, a paste) would throw.
        var container = savedRange.startContainer;
        if (!container || !container.isConnected || !editableOf(container)) {
            savedRange = null;
            return false;
        }

        try {
            var s = window.getSelection();
            s.removeAllRanges();
            s.addRange(savedRange);
            return true;
        } catch (e) {
            savedRange = null;
            return false;
        }
    }

    function focusSurface() {
        var target = activeEditable && activeEditable.isConnected ? activeEditable : surface;

        // Note the selection BEFORE focusing. Calling focus() on an element the
        // selection is already inside still discards that selection in Chrome
        // when the focus is coming back from somewhere else - which is exactly
        // the case for a ribbon dropdown - so the range has to be captured
        // first and put back afterwards, unconditionally.
        if (selectionIsLive()) rememberSelection();

        var needsFocus = document.activeElement !== target && !target.contains(document.activeElement);
        if (needsFocus) target.focus();

        restoreSelection(needsFocus);
    }

    function selection() {
        var s = window.getSelection();
        if (!s || s.rangeCount === 0) return null;

        var editable = editableOf(s.anchorNode);
        if (!editable) return null;

        setActiveEditable(editable);
        return s;
    }

    /** The block-level elements the selection touches - what a paragraph command acts on. */
    function selectedBlocks() {
        var s = selection();
        if (!s) return [];

        var scope = activeEditable && activeEditable.isConnected ? activeEditable : surface;
        var range = s.getRangeAt(0);
        var blocks = [];

        var all = scope.querySelectorAll('p,div,h1,h2,h3,h4,h5,h6,li,td,th,blockquote,pre');

        for (var i = 0; i < all.length; i++) {
            if (all[i].hasAttribute(SPACER)) continue;
            if (range.intersectsNode(all[i])) {
                // Only the innermost block: a <td> containing a <p> should not
                // both be styled, or the indent is applied twice.
                var hasBlockChild = all[i].querySelector('p,div,h1,h2,h3,h4,h5,h6,li,blockquote,pre');
                if (!hasBlockChild) blocks.push(all[i]);
            }
        }

        if (blocks.length === 0) {
            var node = range.startContainer;
            while (node && node !== scope) {
                if (node.nodeType === 1 && /^(P|DIV|H[1-6]|LI|TD|TH|BLOCKQUOTE|PRE)$/.test(node.tagName)) {
                    blocks.push(node);
                    break;
                }
                node = node.parentNode;
            }
        }

        return blocks;
    }

    function eachBlock(apply) {
        if (config.readOnly) { readOnlyRefusal(); return; }

        var blocks = selectedBlocks();
        if (blocks.length === 0) { setStatus('Put the cursor in a paragraph first.', 'warn'); return; }

        for (var i = 0; i < blocks.length; i++) apply(blocks[i]);
        markDirty();
    }

    function exec(command, value) {
        if (config.readOnly) { readOnlyRefusal(); return; }
        focusSurface();

        try {
            document.execCommand(command, false, value === undefined ? null : value);
        } catch (e) {
            setStatus('That formatting is not available in this browser.', 'warn');
        }

        markDirty();
        refreshToolbarState();
    }

    // styleWithCSS makes bold and colour emit <span style> rather than <font>,
    // which is what the server's whitelist accepts and what the .docx writer
    // understands.
    try { document.execCommand('styleWithCSS', false, true); } catch (e) { /* not fatal */ }

    // =====================================================================
    // Named styles
    //
    // A style is a CLASS, not a bundle of inline formatting, because the class
    // maps to a real Word style in the export. A document whose headings are
    // "18pt bold navy" cannot be restyled; one whose headings are Heading 1 can.
    // =====================================================================

    var NAMED_STYLES = {
        'normal': { tag: 'p', cls: null },
        'nospacing': { tag: 'p', cls: 'dms-doc-nospacing' },
        'title': { tag: 'p', cls: 'dms-doc-title' },
        'subtitle': { tag: 'p', cls: 'dms-doc-subtitle' },
        'h1': { tag: 'h1', cls: null },
        'h2': { tag: 'h2', cls: null },
        'h3': { tag: 'h3', cls: null },
        'h4': { tag: 'h4', cls: null },
        'quote': { tag: 'blockquote', cls: null },
        'caption': { tag: 'p', cls: 'dms-doc-caption' },
        'pre': { tag: 'pre', cls: null }
    };

    var STYLE_CLASSES = ['dms-doc-title', 'dms-doc-subtitle', 'dms-doc-caption',
        'dms-doc-nospacing', 'dms-doc-quote'];

    function applyNamedStyle(name) {
        var spec = NAMED_STYLES[name];
        if (!spec) return;

        exec('formatBlock', spec.tag);

        eachBlock(function (block) {
            for (var i = 0; i < STYLE_CLASSES.length; i++) block.classList.remove(STYLE_CLASSES[i]);
            if (spec.cls) block.classList.add(spec.cls);
        });
    }

    // =====================================================================
    // Paragraph formatting the browser has no command for
    // =====================================================================

    function setLineSpacing(value) {
        eachBlock(function (block) {
            if (value === '') block.style.lineHeight = '';
            else block.style.lineHeight = value;
        });
    }

    function setSpaceBefore(points) {
        eachBlock(function (block) { block.style.marginTop = points + 'pt'; });
    }

    function setSpaceAfter(points) {
        eachBlock(function (block) { block.style.marginBottom = points + 'pt'; });
    }

    function setParagraphBorder(on) {
        eachBlock(function (block) {
            block.style.border = on ? '1px solid #808080' : '';
            block.style.padding = on ? '6pt' : '';
        });
    }

    function setParagraphShading(colour) {
        eachBlock(function (block) { block.style.backgroundColor = colour || ''; });
    }

    /**
     * Copies the paragraph and character formatting of the current block and
     * applies it to the next selection - Word's format painter.
     */
    function toggleFormatPainter() {
        if (config.readOnly) { readOnlyRefusal(); return; }

        if (formatPainter) { formatPainter = null; setStatus('Format painter off.', ''); syncPainterButton(); return; }

        focusSurface();

        var blocks = selectedBlocks();
        if (blocks.length === 0) { setStatus('Put the cursor in the paragraph to copy from.', 'warn'); return; }

        var source = blocks[0];

        // Word's painter carries the character formatting as well as the
        // paragraph's. Copying only the paragraph meant painting a 14pt Arial
        // heading onto a paragraph left it 11pt Calibri, which looks like the
        // painter did nothing.
        var text = characterSourceIn(source);
        var computed = text ? window.getComputedStyle(text) : null;

        formatPainter = {
            cssText: source.style.cssText,
            classes: STYLE_CLASSES.filter(function (c) { return source.classList.contains(c); }),
            tag: source.tagName.toLowerCase(),
            character: computed ? {
                fontFamily: computed.fontFamily,
                fontSize: computed.fontSize,
                fontWeight: computed.fontWeight,
                fontStyle: computed.fontStyle,
                textDecorationLine: computed.textDecorationLine,
                color: computed.color
            } : null
        };

        setStatus('Format painter on - select the text to paint.', 'busy');
        syncPainterButton();
    }

    /** The element whose character formatting the painter should copy. */
    function characterSourceIn(block) {
        var s = window.getSelection();
        var anchor = s && s.rangeCount ? s.anchorNode : null;
        var element = anchor ? (anchor.nodeType === 1 ? anchor : anchor.parentNode) : null;

        if (element && block.contains(element)) return element;
        return block;
    }

    function syncPainterButton() {
        var button = root.querySelector('[data-dms-cmd="format-painter"]');
        if (button) button.classList.toggle('is-active', !!formatPainter);
    }

    function applyFormatPainter() {
        if (!formatPainter) return;

        var painter = formatPainter;
        formatPainter = null;
        syncPainterButton();

        if (/^(p|h1|h2|h3|h4|blockquote|pre)$/.test(painter.tag)) exec('formatBlock', painter.tag);

        eachBlock(function (block) {
            block.style.cssText = painter.cssText;
            for (var i = 0; i < STYLE_CLASSES.length; i++) block.classList.remove(STYLE_CLASSES[i]);
            for (var j = 0; j < painter.classes.length; j++) block.classList.add(painter.classes[j]);
        });

        paintCharacterFormatting(painter.character);

        setStatus('Formatting applied.', '');
    }

    /**
     * Puts the painted character formatting onto whatever is selected.
     *
     * Written as a span wrapping the selection rather than as execCommand
     * calls, because execCommand('fontSize') cannot express point sizes and the
     * export needs real points (see applyFontSize).
     */
    function paintCharacterFormatting(character) {
        if (!character) return;

        var s = selection();
        if (!s || s.isCollapsed) return;

        var span = document.createElement('span');
        span.style.fontFamily = character.fontFamily;
        span.style.fontSize = Math.round(parseFloat(character.fontSize) * 72 / 96 * 10) / 10 + 'pt';
        span.style.fontWeight = character.fontWeight;
        span.style.fontStyle = character.fontStyle;
        span.style.color = character.color;
        if (character.textDecorationLine && character.textDecorationLine !== 'none') {
            span.style.textDecoration = character.textDecorationLine;
        }

        try {
            var range = s.getRangeAt(0);
            span.appendChild(range.extractContents());
            range.insertNode(span);

            // Leave the painted text selected, as Word does.
            var after = document.createRange();
            after.selectNodeContents(span);
            s.removeAllRanges();
            s.addRange(after);
            rememberSelection();
        } catch (e) {
            // A selection spanning several blocks cannot be wrapped in one
            // span; the paragraph formatting above has already been applied.
        }
    }

    // =====================================================================
    // Insert
    // =====================================================================

    function insertHtml(html) {
        if (config.readOnly) { readOnlyRefusal(); return; }
        focusSurface();

        try {
            document.execCommand('insertHTML', false, html);
        } catch (e) {
            setStatus('That could not be inserted in this browser.', 'warn');
            return;
        }

        markDirty();
    }

    function insertNodeAtCursor(node) {
        var s = selection();

        if (!s) {
            var target = activeEditable && activeEditable.isConnected ? activeEditable : surface;
            target.appendChild(node);
            if (target === surface) target.appendChild(emptyParagraph());
            return;
        }

        var range = s.getRangeAt(0);
        range.deleteContents();
        range.insertNode(node);
        range.setStartAfter(node);
        range.collapse(true);
        s.removeAllRanges();
        s.addRange(range);
    }

    function emptyParagraph() {
        var p = document.createElement('p');
        p.appendChild(document.createElement('br'));
        return p;
    }

    function insertHyperlink() {
        var s = selection();
        var existing = s && !s.isCollapsed ? s.toString() : '';

        var url = window.prompt('Web address (https://… or mailto:…)', 'https://');
        if (!url) return;

        if (!/^(https?:\/\/|mailto:)/i.test(url)) {
            setStatus('Only http, https and mailto addresses can be linked.', 'error');
            return;
        }

        if (existing) exec('createLink', url);
        else insertHtml('<a href="' + escapeHtml(url) + '">' + escapeHtml(url) + '</a>&nbsp;');
    }

    var SYMBOLS = ['°', '±', '×', '÷', '≤', '≥', '≠', '≈', 'µ', 'Ω', 'α', 'β', 'γ', 'Δ', 'λ', 'π',
        '½', '¼', '¾', '™', '®', '©', '§', '¶', '→', '←', '↔', '✓', '✗', '•', '–', '—'];

    function toggleSymbolPalette() {
        var palette = root.querySelector('[data-dms-symbols]');
        if (!palette) return;

        if (palette.childNodes.length === 0) {
            for (var i = 0; i < SYMBOLS.length; i++) {
                var button = document.createElement('button');
                button.type = 'button';
                button.textContent = SYMBOLS[i];
                button.setAttribute('data-dms-symbol', SYMBOLS[i]);
                palette.appendChild(button);
            }
        }

        palette.hidden = !palette.hidden;
    }

    function insertCaption() {
        var text = window.prompt('Caption text', 'Table 1 - ');
        if (text === null) return;
        insertHtml('<p class="dms-doc-caption">' + escapeHtml(text) + '</p>');
    }

    // =====================================================================
    // Auto index and automatic heading numbering
    //
    // The index is BUILT HERE, not left for Word to build on open. A controlled
    // document is reviewed and approved on screen: an index that says
    // "right-click to update" is an index the reviewer cannot check, and the
    // page numbers a reviewer signs for have to be the page numbers the
    // document has. So the entries and their page numbers are generated from
    // the real paginated layout, kept in step as the document is edited, and
    // exported as a Word TOC field whose CACHED RESULT is exactly what was on
    // screen - so Word shows the approved index immediately, and can still
    // refresh it if the document is later edited in Word.
    //
    // The block is contenteditable=false. Generated content that an author
    // could hand-edit is content that can be made to disagree with the document
    // it indexes, which in a GxP system is a defect, not a convenience.
    // =====================================================================

    var TOC_MARKER = 'data-dms-toc';
    var AUTONUM_CLASS = 'dms-doc-autonum';
    var INDEX_LEVELS = 'h1,h2,h3,h4';

    function indexBlock() {
        return surface.querySelector('.dms-doc-toc');
    }

    /** Headings the index covers: everything but the ones inside the index itself. */
    function indexableHeadings() {
        return Array.prototype.filter.call(surface.querySelectorAll(INDEX_LEVELS), function (heading) {
            return !heading.closest('.dms-doc-toc') && !heading.hasAttribute(SPACER);
        });
    }

    /**
     * Re-marks generated content after a round trip through the server.
     *
     * The sanitiser keeps the classes - they are whitelisted - but drops
     * <c>contenteditable</c> and every data attribute, exactly as it should: an
     * editor hint is not document content, and a stored attribute that the
     * client trusts is a stored attribute an attacker can set. So the marks are
     * put back here, from the classes, every time the document is opened.
     *
     * A document saved before the index became a block also gets migrated: the
     * old placeholder was a paragraph, and entries cannot be appended to one.
     */
    function normaliseGeneratedContent() {
        var legacy = surface.querySelectorAll('p.dms-doc-toc');

        for (var l = 0; l < legacy.length; l++) {
            var replacement = document.createElement('div');
            replacement.className = 'dms-doc-toc';

            var title = document.createElement('p');
            title.className = 'dms-doc-toc-title';
            title.textContent = 'Contents';
            replacement.appendChild(title);

            legacy[l].parentNode.replaceChild(replacement, legacy[l]);
        }

        var block = indexBlock();

        if (block) {
            block.setAttribute(TOC_MARKER, '1');
            block.setAttribute('contenteditable', 'false');
        }

        var numbers = surface.querySelectorAll('.' + AUTONUM_CLASS);
        for (var n = 0; n < numbers.length; n++) numbers[n].setAttribute('contenteditable', 'false');
    }

    function insertTableOfContents() {
        if (config.readOnly) return;

        var existing = indexBlock();

        if (existing) {
            rebuildIndex(true);
            setStatus('Index updated from the current headings.', '');
            return;
        }

        var block = document.createElement('div');
        block.className = 'dms-doc-toc';
        block.setAttribute(TOC_MARKER, '1');
        block.setAttribute('contenteditable', 'false');

        var title = document.createElement('p');
        title.className = 'dms-doc-toc-title';
        title.textContent = 'Contents';
        block.appendChild(title);

        // Placed as a sibling of the paragraph the caret is in, not at the
        // caret: a div nested inside a paragraph is invalid markup that renders
        // fine, which is the kind of thing that survives until the export.
        var here = selectedBlocks()[0];
        var anchor = here;

        while (anchor && anchor.parentNode !== surface) anchor = anchor.parentNode;

        if (anchor) surface.insertBefore(block, anchor);
        else surface.appendChild(block);

        // An author needs somewhere to put the cursor after a block that cannot
        // be typed in, or the index becomes a dead end at the top of the page.
        if (!block.nextSibling) surface.appendChild(emptyParagraph());

        rebuildIndex(true);
        markDirty();
        setStatus('Index inserted. It updates itself as you edit the headings.', 'warn');
    }

    /**
     * A cheap fingerprint of what the index depends on. Comparing it is what
     * makes "automatic" affordable: the entries are only rebuilt when the
     * headings actually changed, not on every keystroke.
     */
    function headingSignature() {
        return indexableHeadings().map(function (heading) {
            return heading.tagName + '' + heading.textContent.trim();
        }).join('');
    }

    var lastIndexSignature = null;

    function rebuildIndex(force) {
        var block = indexBlock();
        if (!block) { lastIndexSignature = null; return false; }

        var signature = headingSignature();
        if (!force && signature === lastIndexSignature) return false;

        lastIndexSignature = signature;

        var headings = indexableHeadings();

        // Everything except the title is regenerated.
        var entries = block.querySelectorAll('.dms-doc-toc-entry');
        for (var e = entries.length - 1; e >= 0; e--) entries[e].parentNode.removeChild(entries[e]);

        if (headings.length === 0) {
            var empty = document.createElement('p');
            empty.className = 'dms-doc-toc-entry dms-doc-toc-l1';
            empty.textContent = 'No headings yet - apply Heading 1 to a section title.';
            block.appendChild(empty);
            return true;
        }

        for (var i = 0; i < headings.length; i++) {
            var heading = headings[i];
            var level = parseInt(heading.tagName.substring(1), 10) || 1;

            var entry = document.createElement('p');
            entry.className = 'dms-doc-toc-entry dms-doc-toc-l' + Math.min(4, level);

            var text = document.createElement('span');
            text.className = 'dms-doc-toc-text';
            text.textContent = heading.textContent.trim() || '(untitled section)';

            var pageNumber = document.createElement('span');
            pageNumber.className = 'dms-doc-toc-page';
            pageNumber.textContent = '';

            entry.appendChild(text);
            entry.appendChild(pageNumber);
            block.appendChild(entry);
        }

        return true;
    }

    /**
     * Fills in the page number on each entry from the paginated layout.
     *
     * Run after pagination, because the page a heading is on is a fact about the
     * layout and not about the markup. Returns true if any number changed, which
     * is the caller's cue that the index may have changed height and the flow
     * needs breaking again.
     */
    function refreshIndexPageNumbers(g) {
        var block = indexBlock();
        if (!block) return false;

        var headings = indexableHeadings();
        var cells = block.querySelectorAll('.dms-doc-toc-page');
        var changed = false;

        for (var i = 0; i < cells.length && i < headings.length; i++) {
            var top = topInSurface(headings[i]);
            var page = Math.max(1, Math.floor(top / g.stride) + 1);
            var value = String(page);

            if (cells[i].textContent !== value) { cells[i].textContent = value; changed = true; }
        }

        return changed;
    }

    /**
     * Automatic heading numbering.
     *
     * The number is put INTO the heading as a marked, non-editable span rather
     * than drawn by a CSS counter, because CSS counters do not exist in a .docx:
     * an index and an export that showed different numbers from the screen would
     * be exactly the fidelity defect this editor is meant not to have. The span
     * is regenerated on every pass, so adding, deleting or reordering a section
     * renumbers everything below it.
     */
    function applyAutoNumbers() {
        // A number the author typed is removed first, or the generated one is
        // added in front of it and the heading reads "1 1. Project Details" -
        // which is exactly the defect that was reported.
        adoptTypedNumbers(false);

        var headings = indexableHeadings();
        var counters = [0, 0, 0, 0];
        var changed = false;

        for (var i = 0; i < headings.length; i++) {
            var heading = headings[i];
            var level = Math.min(4, parseInt(heading.tagName.substring(1), 10) || 1);

            // A sub-section that appears before any section above it still has
            // to read sensibly: an ancestor that has not been counted yet counts
            // as the first one, so a stray Heading 2 is "1.1" and never "0.1".
            for (var above = 0; above < level - 1; above++) {
                if (counters[above] === 0) counters[above] = 1;
            }

            counters[level - 1]++;
            for (var deeper = level; deeper < counters.length; deeper++) counters[deeper] = 0;

            changed = setAutoNumber(heading, autoNumberLabel(counters, level)) || changed;
        }

        return changed;
    }

    /**
     * Word's own multilevel format, and the one the customer asked for by
     * example: a top-level section is "1." and everything below it is "1.1",
     * "1.1.1" with no trailing dot.
     */
    function autoNumberLabel(counters, level) {
        var digits = counters.slice(0, level).join('.');
        return digits + (level === 1 ? '.' : '') + ' ';
    }

    // ---- adopting numbers the author typed --------------------------------
    //
    // A number that is part of the text is a number the editor cannot keep in
    // step: delete section 2 and "3. Requirements" still says 3. So when
    // automatic numbering is switched on, a typed number is taken OVER rather
    // than left alone or duplicated - removed from the text and reissued as a
    // generated one. That is what "the numbering should be managed by the
    // document editor rather than being treated as ordinary text" means.

    /**
     * A leading number token, but only where it is unambiguously a section
     * number.
     *
     * "1.2" and "1." and "3)" are section numbers. A bare "2026 review" is not,
     * and stripping the year out of a heading because it happened to start with
     * digits would be a data-loss bug in a controlled document. So a single
     * number must carry a dot or a bracket to qualify; a multi-level one is
     * self-evident.
     */
    var TYPED_MULTILEVEL = /^(\s*)(\d{1,3}(?:\.\d{1,3})+)\s*[.)]?[\s ]+/;
    var TYPED_SINGLE = /^(\s*)(\d{1,3})\s*[.)][\s ]+/;

    function typedNumberOf(heading) {
        var node = firstTextOutsideAutoNumber(heading);
        if (!node) return null;

        var match = TYPED_MULTILEVEL.exec(node.nodeValue) || TYPED_SINGLE.exec(node.nodeValue);
        if (!match) return null;

        return { node: node, length: match[0].length, value: match[2] };
    }

    function firstTextOutsideAutoNumber(heading) {
        var walker = document.createTreeWalker(heading, NodeFilter.SHOW_TEXT, {
            acceptNode: function (node) {
                if (node.parentNode && node.parentNode.closest &&
                    node.parentNode.closest('.' + AUTONUM_CLASS)) {
                    return NodeFilter.FILTER_REJECT;
                }
                return node.nodeValue.trim().length > 0
                    ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
            }
        });

        return walker.nextNode();
    }

    /**
     * Removes typed numbers from every heading, and - when asked - sets the
     * heading's level to the depth the author's own number implies.
     *
     * "1.1 Project Scope" is a second-level section however it happens to be
     * tagged, and numbering it "2." because it was tagged Heading 1 would
     * renumber the author's document into something they did not write. The
     * retag only ever happens on the explicit Automatic numbering click, only
     * to headings that carried a number, and it is reported.
     */
    function adoptTypedNumbers(retag) {
        var headings = indexableHeadings();
        var adopted = 0;
        var relevelled = 0;

        for (var i = 0; i < headings.length; i++) {
            var heading = headings[i];
            var typed = typedNumberOf(heading);
            if (!typed) continue;

            typed.node.nodeValue = typed.node.nodeValue.slice(typed.length);
            adopted++;

            if (!retag) continue;

            var depth = Math.min(4, typed.value.split('.').length);
            var level = parseInt(heading.tagName.substring(1), 10) || 1;

            if (depth !== level) {
                retagHeading(heading, depth);
                relevelled++;
            }
        }

        return { adopted: adopted, relevelled: relevelled };
    }

    function retagHeading(heading, level) {
        var replacement = document.createElement('h' + level);

        while (heading.firstChild) replacement.appendChild(heading.firstChild);

        for (var i = 0; i < heading.attributes.length; i++) {
            var attribute = heading.attributes[i];
            replacement.setAttribute(attribute.name, attribute.value);
        }

        heading.parentNode.replaceChild(replacement, heading);
        return replacement;
    }

    function setAutoNumber(heading, label) {
        var existing = heading.firstChild;
        var span = existing && existing.nodeType === 1 && existing.classList &&
            existing.classList.contains(AUTONUM_CLASS) ? existing : null;

        if (span) {
            if (span.textContent === label) return false;
            span.textContent = label;
            return true;
        }

        span = document.createElement('span');
        span.className = AUTONUM_CLASS;
        span.setAttribute('contenteditable', 'false');
        span.textContent = label;
        heading.insertBefore(span, heading.firstChild);
        return true;
    }

    function clearAutoNumbers() {
        var spans = surface.querySelectorAll('.' + AUTONUM_CLASS);
        for (var i = spans.length - 1; i >= 0; i--) spans[i].parentNode.removeChild(spans[i]);
        return spans.length > 0;
    }

    function toggleAutoNumbering() {
        if (config.readOnly) return;

        pageSetup.autoNumberHeadings = !pageSetup.autoNumberHeadings;

        if (pageSetup.autoNumberHeadings) {
            var taken = adoptTypedNumbers(true);
            applyAutoNumbers();

            var note = 'Headings are numbered automatically. Save to keep the numbering.';

            if (taken.adopted > 0) {
                note = 'Took over ' + taken.adopted + ' number' + (taken.adopted === 1 ? '' : 's') +
                    ' you had typed' +
                    (taken.relevelled > 0
                        ? ', and set ' + taken.relevelled + ' heading level' +
                          (taken.relevelled === 1 ? '' : 's') + ' to match'
                        : '') +
                    '. The editor keeps them in step from now on. Save to keep the change.';
            }

            setStatus(note, 'warn');
        } else {
            clearAutoNumbers();
            setStatus('Automatic heading numbers removed. Save to keep the change.', 'warn');
        }

        // The numbers are part of the heading text, so the index entries change
        // with them.
        rebuildIndex(true);
        markDirty();
        syncAutoNumberButton();
    }

    // =====================================================================
    // Navigation pane
    //
    // Word's left-hand pane: the document's headings, click to jump. In a
    // fifty-page SOP it is the difference between finding section 7.3 and
    // scrolling for it, and it is built from the same outline the auto index
    // and the automatic numbering use - so if the pane looks wrong, the index
    // is wrong too, which is worth knowing.
    // =====================================================================

    function navigationPane() { return root.querySelector('[data-dms-nav]'); }

    function toggleNavigation() {
        var pane = navigationPane();
        if (!pane) return;

        pane.hidden = !pane.hidden;

        var button = root.querySelector('[data-dms-cmd="toggle-navigation"]');
        if (button) {
            button.classList.toggle('is-active', !pane.hidden);
            button.setAttribute('aria-pressed', pane.hidden ? 'false' : 'true');
        }

        if (!pane.hidden) rebuildNavigation();
        reflow();
    }

    function rebuildNavigation() {
        var pane = navigationPane();
        if (!pane || pane.hidden) return;

        var list = pane.querySelector('[data-dms-nav-list]');
        if (!list) return;

        var headings = indexableHeadings();
        list.innerHTML = '';

        if (headings.length === 0) {
            var empty = document.createElement('p');
            empty.className = 'dms-nav-empty';
            empty.textContent = 'No headings yet. Apply Heading 1 to a section title and it will appear here.';
            list.appendChild(empty);
            return;
        }

        for (var i = 0; i < headings.length; i++) {
            var heading = headings[i];
            var level = Math.min(4, parseInt(heading.tagName.substring(1), 10) || 1);

            var item = document.createElement('button');
            item.type = 'button';
            item.className = 'dms-nav-item is-l' + level;
            item.textContent = heading.textContent.trim() || '(untitled section)';
            item.setAttribute('data-dms-nav-index', String(i));

            list.appendChild(item);
        }
    }

    /** Scrolls a heading into view and puts the caret in it. */
    function goToHeading(index) {
        var headings = indexableHeadings();
        var heading = headings[index];
        if (!heading || !canvas) return;

        if (hfMode) exitHeaderFooter();

        var top = topInSurface(heading) + geometryCache.marginTop;
        canvas.scrollTop = Math.max(0, (top * zoom) - 60);

        var range = document.createRange();
        range.selectNodeContents(heading);
        range.collapse(true);

        var selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);

        if (!config.readOnly) surface.focus();
    }

    root.addEventListener('click', function (event) {
        var item = event.target.closest ? event.target.closest('[data-dms-nav-index]') : null;
        if (!item || !root.contains(item)) return;

        event.preventDefault();
        goToHeading(parseInt(item.getAttribute('data-dms-nav-index'), 10) || 0);
    });

    function syncAutoNumberButton() {
        var button = root.querySelector('[data-dms-cmd="auto-number"]');
        if (!button) return;

        var on = !!pageSetup.autoNumberHeadings;
        button.classList.toggle('is-active', on);
        button.setAttribute('aria-pressed', on ? 'true' : 'false');
    }

    // =====================================================================
    // Tables
    // =====================================================================

    var lastCell = null;

    function currentCell() {
        var s = selection();

        if (s) {
            var node = s.getRangeAt(0).startContainer;
            while (node && node !== surface) {
                if (node.nodeType === 1 && (node.tagName === 'TD' || node.tagName === 'TH')) {
                    if (!node.classList.contains('dms-page-spacer-cell')) { lastCell = node; return node; }
                }
                node = node.parentNode;
            }
        }

        // Several table operations REPLACE cells rather than mutate them, and a
        // replaced cell takes the selection with it. Browser testing found
        // exactly that: after "make this a header row", the next table button
        // reported "put the cursor inside a table first".
        if (lastCell && surface.contains(lastCell)) return lastCell;

        lastCell = null;
        return null;
    }

    function focusCell(cell) {
        if (!cell) return;
        lastCell = cell;

        var range = document.createRange();
        range.selectNodeContents(cell);
        range.collapse(true);

        var s = window.getSelection();
        if (!s) return;
        s.removeAllRanges();
        s.addRange(range);
    }

    function tableOf(cell) {
        var node = cell;
        while (node && node !== surface) {
            if (node.nodeType === 1 && node.tagName === 'TABLE') return node;
            node = node.parentNode;
        }
        return null;
    }

    function needTable() { setStatus('Put the cursor inside a table first.', 'warn'); }

    function needHeaderFooter() {
        setStatus('Open the header or footer first - double-click the top or bottom margin of a page, ' +
            'or use Insert > Header.', 'warn');
    }

    /**
     * Runs a browser command and says so when the browser had nothing to do.
     *
     * Undo and redo with an empty stack were completely silent, which is
     * indistinguishable from a dead button - and "the buttons do nothing" is
     * how it gets reported. queryCommandEnabled tells us which it was.
     */
    function execOrExplain(command, nothingToDo) {
        if (config.readOnly) { readOnlyRefusal(); return; }

        var enabled = true;
        try { enabled = document.queryCommandEnabled(command); } catch (e) { enabled = true; }

        if (!enabled) { setStatus(nothingToDo, 'warn'); return; }
        exec(command);
    }

    function askNumber(question, fallback, min, max) {
        var answer = window.prompt(question, String(fallback));
        if (answer === null) return null;

        var value = parseInt(answer, 10);
        if (isNaN(value) || value < min || value > max) {
            setStatus('Enter a number between ' + min + ' and ' + max + '.', 'warn');
            return null;
        }
        return value;
    }

    function insertTable() {
        if (config.readOnly) return;

        var rows = askNumber('How many rows?', 3, 1, 200);
        if (rows === null) return;

        var columns = askNumber('How many columns?', 3, 1, 20);
        if (columns === null) return;

        var width = (100 / columns).toFixed(4);
        var html = '<table class="dms-doc-table"><colgroup>';

        for (var c = 0; c < columns; c++) html += '<col style="width:' + width + '%">';
        html += '</colgroup><thead><tr>';

        for (var h = 0; h < columns; h++) html += '<th><p><br></p></th>';
        html += '</tr></thead><tbody>';

        for (var r = 1; r < rows; r++) {
            html += '<tr>';
            for (var d = 0; d < columns; d++) html += '<td><p><br></p></td>';
            html += '</tr>';
        }

        html += '</tbody></table><p><br></p>';
        insertHtml(html);
    }

    function realCells(row) {
        return Array.prototype.filter.call(row.cells, function (c) {
            return !c.classList.contains('dms-page-spacer-cell');
        });
    }

    function addRow(above) {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var row = cell.parentNode;
        var fresh = document.createElement('tr');

        for (var i = 0; i < realCells(row).length; i++) {
            var td = document.createElement('td');
            td.appendChild(emptyParagraph());
            fresh.appendChild(td);
        }

        row.parentNode.insertBefore(fresh, above ? row : row.nextSibling);
        focusCell(fresh.cells[Math.min(cell.cellIndex, fresh.cells.length - 1)]);
        markDirty();
    }

    function addColumn(before) {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var index = cell.cellIndex;
        var table = tableOf(cell);
        if (!table) return;

        for (var r = 0; r < table.rows.length; r++) {
            var row = table.rows[r];
            if (row.hasAttribute(SPACER)) continue;

            var reference = row.cells[Math.min(index, row.cells.length - 1)];
            var fresh = document.createElement(reference && reference.tagName === 'TH' ? 'th' : 'td');
            fresh.appendChild(emptyParagraph());

            if (before) row.insertBefore(fresh, row.cells[index] || null);
            else row.insertBefore(fresh, row.cells[index] ? row.cells[index].nextSibling : null);
        }

        rebalanceColumns(table);
        markDirty();
    }

    function deleteRow() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var row = cell.parentNode;
        var table = tableOf(cell);
        if (!table) return;

        if (realRows(table).length <= 1) {
            setStatus('That is the last row - delete the table instead.', 'warn');
            return;
        }

        var index = Array.prototype.indexOf.call(table.rows, row);
        row.parentNode.removeChild(row);

        var survivor = table.rows[Math.min(index, table.rows.length - 1)];
        focusCell(survivor ? survivor.cells[Math.min(cell.cellIndex, survivor.cells.length - 1)] : null);
        markDirty();
    }

    function realRows(table) {
        return Array.prototype.filter.call(table.rows, function (r) { return !r.hasAttribute(SPACER); });
    }

    function deleteColumn() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var index = cell.cellIndex;
        var table = tableOf(cell);
        if (!table) return;

        if (table.rows[0] && realCells(table.rows[0]).length <= 1) {
            setStatus('That is the last column - delete the table instead.', 'warn');
            return;
        }

        for (var r = 0; r < table.rows.length; r++) {
            var row = table.rows[r];
            if (row.hasAttribute(SPACER)) continue;
            if (row.cells[index]) row.deleteCell(index);
        }

        rebalanceColumns(table);
        var firstRow = table.rows[0];
        focusCell(firstRow ? firstRow.cells[Math.min(index, firstRow.cells.length - 1)] : null);
        markDirty();
    }

    function deleteTable() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var table = tableOf(cell);
        if (!table) return;
        if (!window.confirm('Delete this entire table and everything in it?')) return;

        table.parentNode.removeChild(table);
        lastCell = null;
        markDirty();
    }

    /**
     * Merges this cell with the one to its right, or the one below.
     *
     * Word lets you select a rectangle and merge it; that needs a cell-selection
     * model this editor does not have, and a half-working rectangular merge
     * produces tables the .docx writer cannot express. Merge-right and
     * merge-down, repeated, reach the same result and can never leave the table
     * in a state the exporter has to guess about.
     */
    function mergeCell(direction) {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        if (direction === 'right') {
            var next = cell.nextElementSibling;
            if (!next) { setStatus('There is no cell to the right.', 'warn'); return; }

            var span = (parseInt(cell.getAttribute('colspan') || '1', 10)) +
                (parseInt(next.getAttribute('colspan') || '1', 10));

            moveChildren(next, cell);
            next.parentNode.removeChild(next);
            cell.setAttribute('colspan', String(span));
            markDirty();
            return;
        }

        var table = tableOf(cell);
        if (!table) return;

        var row = cell.parentNode;
        var rowIndex = Array.prototype.indexOf.call(table.rows, row);
        var below = null;

        for (var r = rowIndex + 1; r < table.rows.length; r++) {
            if (table.rows[r].hasAttribute(SPACER)) continue;
            below = table.rows[r];
            break;
        }

        if (!below) { setStatus('There is no row below.', 'warn'); return; }

        var target = below.cells[cell.cellIndex];
        if (!target) { setStatus('There is no cell below.', 'warn'); return; }

        var rowSpan = (parseInt(cell.getAttribute('rowspan') || '1', 10)) +
            (parseInt(target.getAttribute('rowspan') || '1', 10));

        moveChildren(target, cell);
        target.parentNode.removeChild(target);
        cell.setAttribute('rowspan', String(rowSpan));
        markDirty();
    }

    function moveChildren(from, to) {
        var text = (from.textContent || '').trim();
        if (text.length === 0) return;

        while (from.firstChild) to.appendChild(from.firstChild);
    }

    function splitCell() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var colspan = parseInt(cell.getAttribute('colspan') || '1', 10);
        var rowspan = parseInt(cell.getAttribute('rowspan') || '1', 10);

        if (colspan > 1) {
            cell.setAttribute('colspan', String(colspan - 1));
            if (colspan - 1 === 1) cell.removeAttribute('colspan');

            var fresh = document.createElement(cell.tagName.toLowerCase());
            fresh.appendChild(emptyParagraph());
            cell.parentNode.insertBefore(fresh, cell.nextSibling);
            markDirty();
            return;
        }

        if (rowspan > 1) {
            cell.setAttribute('rowspan', String(rowspan - 1));
            if (rowspan - 1 === 1) cell.removeAttribute('rowspan');
            markDirty();
            return;
        }

        setStatus('That cell is not merged.', 'warn');
    }

    function toggleHeaderRow() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var table = tableOf(cell);
        if (!table) return;

        var rows = realRows(table);
        if (rows.length === 0) return;

        var first = rows[0];
        var makeHeader = first.cells.length > 0 && first.cells[0].tagName === 'TD';
        var replacementForCaret = null;

        for (var i = first.cells.length - 1; i >= 0; i--) {
            var old = first.cells[i];
            var fresh = document.createElement(makeHeader ? 'th' : 'td');

            while (old.firstChild) fresh.appendChild(old.firstChild);
            if (old.getAttribute('colspan')) fresh.setAttribute('colspan', old.getAttribute('colspan'));
            if (old.getAttribute('rowspan')) fresh.setAttribute('rowspan', old.getAttribute('rowspan'));

            if (old === cell) replacementForCaret = fresh;
            old.parentNode.replaceChild(fresh, old);
        }

        // Keep the first row inside a <thead>, which is what tells the exporter
        // to repeat it at the top of every page the table spans.
        if (makeHeader && first.parentNode.tagName !== 'THEAD') {
            var thead = document.createElement('thead');
            table.insertBefore(thead, table.firstChild);
            thead.appendChild(first);
        } else if (!makeHeader && first.parentNode.tagName === 'THEAD') {
            var body = table.tBodies.length > 0 ? table.tBodies[0] : document.createElement('tbody');
            if (!body.parentNode) table.appendChild(body);
            body.insertBefore(first, body.firstChild);
        }

        focusCell(replacementForCaret || realRows(table)[0].cells[0]);
        markDirty();
    }

    function toggleTableBorders() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var table = tableOf(cell);
        if (!table) return;

        table.classList.toggle('dms-doc-borderless');
        markDirty();
    }

    function setCellShading(colour) {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var blocks = selectedBlocks();
        var cells = blocks.filter(function (b) { return b.tagName === 'TD' || b.tagName === 'TH'; });
        if (cells.length === 0) cells = [cell];

        for (var i = 0; i < cells.length; i++) cells[i].style.backgroundColor = colour || '';
        markDirty();
    }

    function setCellAlign(value) {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        cell.style.verticalAlign = value;
        markDirty();
    }

    function setColumnWidth() {
        var cell = currentCell();
        if (!cell) { needTable(); return; }

        var table = tableOf(cell);
        if (!table) return;

        var percent = askNumber('Column width, as a percentage of the text width', 25, 3, 100);
        if (percent === null) return;

        var group = ensureColgroup(table);
        var col = group.children[cell.cellIndex];
        if (!col) return;

        col.style.width = percent + '%';
        markDirty();
    }

    function ensureColgroup(table) {
        var group = table.querySelector('colgroup');
        var columns = realRows(table).length > 0 ? realCells(realRows(table)[0]).length : 1;

        if (!group) {
            group = document.createElement('colgroup');
            table.insertBefore(group, table.firstChild);
        }

        while (group.children.length < columns) group.appendChild(document.createElement('col'));
        while (group.children.length > columns) group.removeChild(group.lastChild);

        return group;
    }

    // =====================================================================
    // Resizing rows and columns with the mouse
    //
    // Word's model: hover a cell border, the cursor changes, drag it. Dragging a
    // column border moves width between the two columns it separates and leaves
    // the table the same width overall - which matters here more than it does in
    // Word, because the table's width IS the page's text width and a table that
    // grew past it would run off the paper.
    //
    // Both directions are stored where the .docx writer already reads them: the
    // colgroup's percentage widths become w:tblGrid, and a row's inline height
    // becomes w:trHeight with hRule="atLeast" - Word's own semantics for a row
    // border dragged by hand.
    // =====================================================================

    /** How close to a border counts as being on it, in CSS pixels. */
    var GRIP = 5;

    /** No column may be squeezed below this share of the table. */
    var MIN_COLUMN_PERCENT = 4;

    var MIN_ROW_HEIGHT = 16;

    var armed = null;       // what the cursor is over: {kind, table, index|row}
    var dragging = null;    // the resize in progress

    /**
     * The x of each internal column border, by the grid column it follows.
     *
     * Read from the FIRST row, because that is the one row whose cells always
     * line up with the grid: a row further down can be short a cell where a
     * rowspan from above covers it, and counting colspans there would put the
     * borders in the wrong places.
     */
    function columnBorders(table) {
        var rows = realRows(table);
        if (rows.length === 0) return [];

        var cells = realCells(rows[0]);
        var borders = [];
        var index = 0;

        for (var c = 0; c < cells.length; c++) {
            var span = parseInt(cells[c].getAttribute('colspan') || '1', 10) || 1;
            index += span;

            // The last border is the table's own right edge, which is the page's
            // text margin. Dragging it could only make the table too wide.
            if (c < cells.length - 1) {
                borders.push({ index: index, x: cells[c].getBoundingClientRect().right });
            }
        }

        return borders;
    }

    /** What, if anything, the pointer is on the border of. */
    function resizeTargetAt(event) {
        if (config.readOnly) return null;

        var cell = event.target.closest ? event.target.closest('td,th') : null;
        if (!cell || cell.classList.contains('dms-page-spacer-cell')) return null;
        if (!editableOf(cell)) return null;

        var table = tableOf(cell);
        if (!table) return null;

        var borders = columnBorders(table);

        for (var b = 0; b < borders.length; b++) {
            if (Math.abs(event.clientX - borders[b].x) <= GRIP) {
                return { kind: 'column', table: table, index: borders[b].index };
            }
        }

        var rows = realRows(table);

        for (var r = 0; r < rows.length; r++) {
            var box = rows[r].getBoundingClientRect();
            if (Math.abs(event.clientY - box.bottom) <= GRIP &&
                event.clientX >= box.left && event.clientX <= box.right) {
                return { kind: 'row', table: table, row: rows[r] };
            }
        }

        return null;
    }

    function showResizeCursor(target) {
        if (!canvas) return;

        canvas.classList.toggle('is-col-resize', !!target && target.kind === 'column');
        canvas.classList.toggle('is-row-resize', !!target && target.kind === 'row');
    }

    /** Current column widths as percentages, from the colgroup or an equal split. */
    function columnPercentages(table, columns) {
        var group = ensureColgroup(table);
        var widths = [];

        for (var i = 0; i < columns; i++) {
            var col = group.children[i];
            var declared = col ? parseFloat(col.style.width) : NaN;
            widths.push(isFinite(declared) && declared > 0 ? declared : 100 / columns);
        }

        // Normalise, so that a table whose widths were never set - or were set in
        // pixels by a paste - starts from a total of exactly 100.
        var total = widths.reduce(function (a, b) { return a + b; }, 0);
        if (total <= 0) return widths;

        return widths.map(function (w) { return (w / total) * 100; });
    }

    function gridColumnCount(table) {
        var rows = realRows(table);
        if (rows.length === 0) return 1;

        var cells = realCells(rows[0]);
        var count = 0;

        for (var c = 0; c < cells.length; c++) {
            count += parseInt(cells[c].getAttribute('colspan') || '1', 10) || 1;
        }

        return Math.max(1, count);
    }

    if (canvas) {
        canvas.addEventListener('mousemove', function (event) {
            if (dragging) return;
            armed = resizeTargetAt(event);
            showResizeCursor(armed);
        });

        canvas.addEventListener('mouseleave', function () {
            if (dragging) return;
            armed = null;
            showResizeCursor(null);
        });

        canvas.addEventListener('mousedown', function (event) {
            if (!armed || config.readOnly) return;

            // Without this the drag starts a text selection across the table.
            event.preventDefault();

            var table = armed.table;

            if (armed.kind === 'column') {
                var columns = gridColumnCount(table);

                dragging = {
                    kind: 'column',
                    table: table,
                    index: armed.index,
                    startX: event.clientX,
                    tableWidth: table.getBoundingClientRect().width / zoom,
                    widths: columnPercentages(table, columns),
                    columns: columns
                };
            } else {
                dragging = {
                    kind: 'row',
                    row: armed.row,
                    startY: event.clientY,
                    startHeight: armed.row.getBoundingClientRect().height / zoom
                };
            }

            document.addEventListener('mousemove', dragResize);
            document.addEventListener('mouseup', endResize);
        });
    }

    function dragResize(event) {
        if (!dragging) return;

        if (dragging.kind === 'column') {
            var left = dragging.index - 1;      // the border sits between these two
            var right = dragging.index;

            if (left < 0 || right >= dragging.columns) return;

            var delta = ((event.clientX - dragging.startX) / zoom / dragging.tableWidth) * 100;

            // Neither neighbour may be squeezed out of existence, so the drag
            // stops at the point where one of them reaches its minimum.
            var maxRight = dragging.widths[right] - MIN_COLUMN_PERCENT;
            var maxLeft = dragging.widths[left] - MIN_COLUMN_PERCENT;
            delta = Math.min(delta, maxRight);
            delta = Math.max(delta, -maxLeft);

            var group = ensureColgroup(dragging.table);
            if (group.children[left]) {
                group.children[left].style.width = (dragging.widths[left] + delta).toFixed(3) + '%';
            }
            if (group.children[right]) {
                group.children[right].style.width = (dragging.widths[right] - delta).toFixed(3) + '%';
            }

            return;
        }

        var height = Math.max(MIN_ROW_HEIGHT,
            Math.round(dragging.startHeight + (event.clientY - dragging.startY) / zoom));

        dragging.row.style.height = height + 'px';
    }

    function endResize() {
        document.removeEventListener('mousemove', dragResize);
        document.removeEventListener('mouseup', endResize);

        if (!dragging) return;

        var kind = dragging.kind;
        dragging = null;

        markDirty();
        reflow();

        setStatus(kind === 'column'
            ? 'Column width changed. Save to keep it.'
            : 'Row height changed. Save to keep it.', 'warn');
    }

    function rebalanceColumns(table) {
        var group = table.querySelector('colgroup');
        if (!group) return;

        var columns = realRows(table).length > 0 ? realCells(realRows(table)[0]).length : 1;
        ensureColgroup(table);

        var width = (100 / columns).toFixed(4);
        for (var i = 0; i < group.children.length; i++) group.children[i].style.width = width + '%';
    }

    // =====================================================================
    // Page setup
    // =====================================================================

    function openPageSetup() {
        var dialog = root.querySelector('[data-dms-pagesetup]');
        if (!dialog) return;

        dialog.hidden = !dialog.hidden;
        if (dialog.hidden) return;

        setField(dialog, 'pageSize', pageSetup.pageSize);
        setField(dialog, 'orientation', pageSetup.orientation);
        setField(dialog, 'columns', String(pageSetup.columns || 1));
        setField(dialog, 'marginTop', mmOf(pageSetup.marginTopTwips));
        setField(dialog, 'marginBottom', mmOf(pageSetup.marginBottomTwips));
        setField(dialog, 'marginLeft', mmOf(pageSetup.marginLeftTwips));
        setField(dialog, 'marginRight', mmOf(pageSetup.marginRightTwips));
        setField(dialog, 'gutter', mmOf(pageSetup.gutterTwips || 0));
    }

    function mmOf(twips) { return (twips / 56.6929).toFixed(1); }

    function twipsOfMm(mm) { return Math.round(parseFloat(mm || '0') * 56.6929); }

    function setField(scope, name, value) {
        var field = scope.querySelector('[data-dms-field="' + name + '"]');
        if (field) field.value = value;
    }

    function readField(scope, name) {
        var field = scope.querySelector('[data-dms-field="' + name + '"]');
        return field ? field.value : '';
    }

    function applyPageSetup() {
        var dialog = root.querySelector('[data-dms-pagesetup]');
        if (!dialog) return;

        pageSetup.pageSize = readField(dialog, 'pageSize') || 'A4';
        pageSetup.orientation = readField(dialog, 'orientation') || 'portrait';
        pageSetup.columns = parseInt(readField(dialog, 'columns') || '1', 10) || 1;
        pageSetup.marginTopTwips = twipsOfMm(readField(dialog, 'marginTop'));
        pageSetup.marginBottomTwips = twipsOfMm(readField(dialog, 'marginBottom'));
        pageSetup.marginLeftTwips = twipsOfMm(readField(dialog, 'marginLeft'));
        pageSetup.marginRightTwips = twipsOfMm(readField(dialog, 'marginRight'));
        pageSetup.gutterTwips = twipsOfMm(readField(dialog, 'gutter'));

        dialog.hidden = true;
        markDirty();
        reflow();
        setStatus('Page setup applied - save to keep it.', 'warn');
    }

    function setMarginPreset(preset) {
        var presets = {
            normal: [1440, 1440, 1440, 1440],
            narrow: [720, 720, 720, 720],
            moderate: [1440, 1080, 1440, 1080],
            wide: [1440, 2880, 1440, 2880]
        };

        var m = presets[preset];
        if (!m) return;

        pageSetup.marginTopTwips = m[0];
        pageSetup.marginRightTwips = m[1];
        pageSetup.marginBottomTwips = m[2];
        pageSetup.marginLeftTwips = m[3];

        markDirty();
        reflow();
    }

    function setOrientation(value) {
        pageSetup.orientation = value;
        markDirty();
        reflow();
    }

    function setPageSize(value) {
        pageSetup.pageSize = value;
        markDirty();
        reflow();
    }

    function setColumns(count) {
        pageSetup.columns = count;
        markDirty();
        reflow();
    }

    function setStyleSet(value) {
        pageSetup.styleSet = value;
        surface.setAttribute('data-style-set', value);
        markDirty();
        setStatus('Style set changed - save to keep it.', 'warn');
    }

    // ---------------------------------------------------------------------
    // Entering and leaving the header and footer
    // ---------------------------------------------------------------------

    /**
     * Opens a band for editing on a given page, the way double-clicking a
     * header does in Word: the band becomes editable in place, the body dims
     * and stops accepting the caret, and the ribbon shows the Header & Footer
     * tab.
     *
     * The body is locked rather than merely dimmed because two editable regions
     * on screen at once is how an author ends up typing a header into a
     * paragraph. Word does the same thing for the same reason.
     */
    function enterHeaderFooter(kind, pageNumber) {
        if (config.readOnly) return;

        hfMode = kind;
        hfPage = Math.max(1, Math.min(pageCountCache, pageNumber || hfPage || 1));

        var variant = variantForPage(hfPage);
        var band = liveBand(kind);

        band.innerHTML = bandHtml(kind, variant);
        band.hidden = false;

        surface.setAttribute('contenteditable', 'false');
        surface.classList.add('is-hf-editing');
        root.classList.add('is-hf-editing');

        clearImageSelection();
        setActiveEditable(band);

        reflow();

        band.focus();
        placeCaretAtEnd(band);

        syncContextualTabs();
        selectTab('headerfooter');
        syncHeaderFooterPanel();

        setStatus(hfLabel(kind, variant) + ' - type on the page; press Escape or Close to go back to the body.',
            'warn');
    }

    function exitHeaderFooter() {
        if (!hfMode) { needHeaderFooter(); return; }

        commitLiveBand();

        hfMode = null;
        liveBands.header && (liveBands.header.hidden = true);
        liveBands.footer && (liveBands.footer.hidden = true);

        if (!config.readOnly) surface.setAttribute('contenteditable', 'true');
        surface.classList.remove('is-hf-editing');
        root.classList.remove('is-hf-editing');

        clearImageSelection();
        setActiveEditable(surface);

        syncContextualTabs();
        selectTab('home');
        reflow();

        surface.focus();
        setStatus(dirty ? 'Unsaved changes' : 'Ready', dirty ? 'warn' : '');
    }

    /** Writes the live band back into the page setup. */
    function commitLiveBand() {
        if (!hfMode) return;

        var band = liveBands[hfMode];
        if (!band) return;

        setBandHtml(hfMode, variantForPage(hfPage), runningContentOf(band));
    }

    function hfLabel(kind, variant) {
        var which = kind === 'header' ? 'Header' : 'Footer';

        if (variant === 'first') return 'First page ' + which.toLowerCase();
        if (variant === 'even') return 'Even page ' + which.toLowerCase();
        return which;
    }

    /**
     * What a running band holds, or null if it holds nothing.
     *
     * Emptiness cannot be decided on text alone: the commonest thing in a
     * header is a site logo and nothing else, and testing textContent would
     * throw that away silently.
     */
    function runningContentOf(field) {
        if (!field) return null;

        var hasText = field.textContent.trim().length > 0;
        var hasObject = !!field.querySelector('img,hr,table');

        return (hasText || hasObject) ? field.innerHTML : null;
    }

    function placeCaretAtEnd(element) {
        var range = document.createRange();
        range.selectNodeContents(element);
        range.collapse(false);

        var selection = window.getSelection();
        selection.removeAllRanges();
        selection.addRange(range);
    }

    /**
     * The other pages follow the band as it is typed in, so an author sees the
     * header they are writing on every page at once - and a picture dropped
     * into a footer is sized against the page it will print on.
     */
    root.addEventListener('input', function (event) {
        var field = event.target.closest ? event.target.closest('[data-dms-hf]') : null;
        if (!field || !root.contains(field)) return;

        commitLiveBand();
        markDirty();
    });

    /**
     * Double-clicking the top or bottom margin opens the header or footer, and
     * double-clicking the body closes them again. This is Word's only
     * discoverable way in, and the one every Word user tries first.
     *
     * Done by coordinate rather than by putting a click target in the margin,
     * so that a single click in the margin still does what it does now - place
     * the caret at the nearest point in the text.
     */
    if (canvas) {
        canvas.addEventListener('dblclick', function (event) {
            if (config.readOnly) return;
            if (event.target.closest('[data-dms-hf]')) return;      // already in the band

            var hit = pageRegionAt(event.clientX, event.clientY);

            if (!hit) return;

            if (hit.region === 'header') { event.preventDefault(); enterHeaderFooter('header', hit.page); }
            else if (hit.region === 'footer') { event.preventDefault(); enterHeaderFooter('footer', hit.page); }
            else if (hfMode) { event.preventDefault(); exitHeaderFooter(); }
        });
    }

    /**
     * Which page, and which part of it, a viewport point falls in.
     *
     * The arithmetic is the same the sheets are drawn with, so "the header
     * area" on screen is exactly the strip the .docx reserves for the header.
     */
    function pageRegionAt(clientX, clientY) {
        if (!pagesBox) return null;

        var box = pagesBox.getBoundingClientRect();
        if (clientX < box.left || clientX > box.right) return null;

        var g = geometry();
        var y = (clientY - box.top) / zoom;
        if (y < 0) return null;

        var stride = g.pageHeight + PAGE_GAP;
        var page = Math.floor(y / stride) + 1;
        var offset = y - ((page - 1) * stride);

        if (page < 1 || page > pageCountCache) return null;
        if (offset > g.pageHeight) return { page: page, region: 'gap' };

        if (offset <= g.marginTop) return { page: page, region: 'header' };
        if (offset >= g.pageHeight - g.marginBottom) return { page: page, region: 'footer' };

        return { page: page, region: 'body' };
    }

    // ---- the Header & Footer ribbon panel --------------------------------

    function syncHeaderFooterPanel() {
        var panel = root.querySelector('[data-dms-panel="headerfooter"]');
        if (!panel) return;

        var where = panel.querySelector('[data-dms-hf-where]');
        if (where) {
            where.textContent = hfMode
                ? hfLabel(hfMode, variantForPage(hfPage)) + ' · page ' + hfPage
                : 'Not in a header or footer';
        }

        setField(panel, 'pageNumberPosition', pageSetup.pageNumberPosition || 'FOOTER_CENTER');
        setField(panel, 'pageNumberFormat', pageSetup.pageNumberFormat || 'PAGE_X_OF_Y');
        setField(panel, 'headerDistance', mmOf(pageSetup.headerDistanceTwips));
        setField(panel, 'footerDistance', mmOf(pageSetup.footerDistanceTwips));

        check(panel, 'showPageNumbers', pageSetup.showPageNumbers);
        check(panel, 'differentFirstPage', pageSetup.differentFirstPage);
        check(panel, 'differentOddEven', pageSetup.differentOddEven);

        function check(scope, name, value) {
            var box = scope.querySelector('[data-dms-field="' + name + '"]');
            if (box) box.checked = !!value;
        }
    }

    /** Reads every setting in the Header & Footer panel back into the page setup. */
    function applyHeaderFooterSettings() {
        var panel = root.querySelector('[data-dms-panel="headerfooter"]');
        if (!panel) return;

        commitLiveBand();

        pageSetup.pageNumberPosition = readField(panel, 'pageNumberPosition') || 'FOOTER_CENTER';
        pageSetup.pageNumberFormat = readField(panel, 'pageNumberFormat') || 'PAGE_X_OF_Y';

        var headerDistance = twipsOfMm(readField(panel, 'headerDistance'));
        var footerDistance = twipsOfMm(readField(panel, 'footerDistance'));

        if (headerDistance > 0) pageSetup.headerDistanceTwips = headerDistance;
        if (footerDistance > 0) pageSetup.footerDistanceTwips = footerDistance;

        pageSetup.showPageNumbers = checked(panel, 'showPageNumbers');
        pageSetup.differentFirstPage = checked(panel, 'differentFirstPage');
        pageSetup.differentOddEven = checked(panel, 'differentOddEven');

        // The variant that applies to the page being edited can have just
        // changed under the author - switching on "different first page" while
        // editing page 1's header moves them from the default band to the first
        // page's. Reload the band so they are editing what the page now shows.
        if (hfMode) {
            var band = liveBands[hfMode];
            if (band) band.innerHTML = bandHtml(hfMode, variantForPage(hfPage));
        }

        markDirty();
        reflow();
        syncHeaderFooterPanel();
        setStatus('Header and footer settings applied - save to keep them.', 'warn');

        function checked(scope, name) {
            var box = scope.querySelector('[data-dms-field="' + name + '"]');
            return box ? box.checked : false;
        }
    }

    function removeRunning(kind) {
        if (config.readOnly) return;

        for (var i = 0; i < RUNNING_VARIANTS.length; i++) setBandHtml(kind, RUNNING_VARIANTS[i], null);

        var band = liveBands[kind];
        if (band) band.innerHTML = '';

        markDirty();
        reflow();
        setStatus('The ' + kind + ' has been removed from every page. Save to keep the change.', 'warn');
    }

    /** Moves between the header and the footer of the page being edited, as Word's buttons do. */
    function gotoRunning(kind) {
        if (!hfMode) { enterHeaderFooter(kind, 1); return; }
        if (hfMode === kind) { liveBands[kind].focus(); return; }

        commitLiveBand();
        liveBands[hfMode].hidden = true;
        enterHeaderFooter(kind, hfPage);
    }

    /** Steps to the previous or next page's band - Word's Previous/Next buttons. */
    function stepRunningPage(delta) {
        if (!hfMode) { needHeaderFooter(); return; }

        var target = Math.max(1, Math.min(pageCountCache, hfPage + delta));
        if (target === hfPage) { setStatus('There is no further page in that direction.', 'warn'); return; }

        commitLiveBand();
        hfPage = target;
        enterHeaderFooter(hfMode, target);
    }

    // =====================================================================
    // Pictures
    //
    // A picture in a controlled document is not decoration: it is a logo in a
    // header, a flow chart in a procedure, an equipment photograph in a work
    // instruction. Authors need to size and place it, and - the reported defect
    // - they need to be able to do that to a picture in the FOOTER, where the
    // site logo goes.
    //
    // Size is carried as the img element's width and height ATTRIBUTES in CSS
    // pixels, not as a style. The .docx writer reads those attributes and turns
    // them into EMU, so what the author drags to on screen is the size Word
    // shows. A style would be dropped by the writer and the export would come
    // back at the picture's natural size, which is the class of difference
    // between screen and file this whole exercise exists to remove.
    // =====================================================================

    var selectedImage = null;
    var replaceTarget = null;
    var imageHandles = null;

    function pictureFields() {
        return {
            width: root.querySelector('[data-dms-picture="width"]'),
            height: root.querySelector('[data-dms-picture="height"]'),
            lock: root.querySelector('[data-dms-picture="lock"]'),
            alt: root.querySelector('[data-dms-picture="alt"]')
        };
    }

    function naturalSizeOf(img) {
        return {
            width: img.naturalWidth || img.offsetWidth || 0,
            height: img.naturalHeight || img.offsetHeight || 0
        };
    }

    function displayedSizeOf(img) {
        return {
            width: Math.round(img.getBoundingClientRect().width / zoom),
            height: Math.round(img.getBoundingClientRect().height / zoom)
        };
    }

    function selectImage(img) {
        if (selectedImage && selectedImage !== img) selectedImage.classList.remove('is-selected');

        selectedImage = img || null;

        if (selectedImage) {
            selectedImage.classList.add('is-selected');
            setActiveEditable(editableOf(selectedImage) || surface);

            var fields = pictureFields();
            var size = displayedSizeOf(selectedImage);

            if (fields.width) fields.width.value = size.width || '';
            if (fields.height) fields.height.value = size.height || '';
            if (fields.alt) fields.alt.value = selectedImage.getAttribute('alt') || '';
        }

        syncContextualTabs();
        positionImageHandles();
    }

    function clearImageSelection() {
        if (!selectedImage) return;
        selectedImage.classList.remove('is-selected');
        selectedImage = null;
        syncContextualTabs();
        positionImageHandles();
    }

    function needPicture() {
        setStatus('Select a picture first - click it once.', 'warn');
    }

    /**
     * The resize grip.
     *
     * Drawn as an overlay positioned over the picture rather than as a child of
     * it, because an <img> cannot contain anything and wrapping every picture in
     * a positioned span would change the document's own markup - and therefore
     * the export - for the sake of a piece of editing furniture.
     */
    function ensureImageHandles() {
        if (imageHandles) return imageHandles;

        imageHandles = document.createElement('div');
        imageHandles.className = 'dms-img-handles';
        imageHandles.setAttribute('aria-hidden', 'true');
        imageHandles.hidden = true;

        var grip = document.createElement('span');
        grip.className = 'dms-img-grip';
        grip.setAttribute('data-dms-img-grip', '1');
        imageHandles.appendChild(grip);

        root.appendChild(imageHandles);
        return imageHandles;
    }

    function positionImageHandles() {
        var handles = ensureImageHandles();

        if (!selectedImage || !selectedImage.isConnected || config.readOnly) {
            handles.hidden = true;
            return;
        }

        var box = selectedImage.getBoundingClientRect();
        var host = root.getBoundingClientRect();

        // Zero-sized means the picture is in a panel that has been closed, or
        // has not loaded yet; there is nothing to put a grip on.
        if (box.width < 1 || box.height < 1) { handles.hidden = true; return; }

        handles.hidden = false;
        handles.style.top = (box.top - host.top) + 'px';
        handles.style.left = (box.left - host.left) + 'px';
        handles.style.width = box.width + 'px';
        handles.style.height = box.height + 'px';
    }

    /**
     * The widest a picture may be set to: the page's text width.
     *
     * Not an arbitrary cap. The writer scales a picture wider than the text area
     * down to fit the paper, so allowing a wider one on screen would guarantee
     * the export disagreed with the editor - and the editor's own max-width
     * rule would meanwhile squash it against a fixed height. Refusing the width
     * in the first place keeps the two the same.
     */
    function maxPictureWidth() {
        return Math.max(64, Math.floor(geometry().contentWidth));
    }

    function setPictureSize(width, height) {
        if (!selectedImage) { needPicture(); return; }

        var natural = naturalSizeOf(selectedImage);
        var ratio = natural.width > 0 && natural.height > 0 ? natural.height / natural.width : 0;
        var fields = pictureFields();
        var locked = !fields.lock || fields.lock.checked;

        var w = Math.min(maxPictureWidth(), Math.round(width || 0));
        var h = Math.round(height || 0);

        // Pressing Apply with both boxes empty used to do nothing at all and
        // say nothing either, which reads as a dead button.
        if (w <= 0 && h <= 0) {
            setStatus('Type a width or a height in pixels first, then press Apply.', 'warn');
            return;
        }

        if (w > 0 && locked && ratio > 0) h = Math.round(w * ratio);
        else if (h > 0 && w <= 0 && locked && ratio > 0) w = Math.round(h / ratio);

        if (w > 0) selectedImage.setAttribute('width', String(Math.max(8, w)));
        if (h > 0) selectedImage.setAttribute('height', String(Math.min(4000, Math.max(8, h))));

        // A style width would win over the attribute on screen and be dropped on
        // export, so any inherited one is removed rather than fought with.
        selectedImage.style.width = '';
        selectedImage.style.height = '';

        afterPictureChange();
    }

    function resetPictureSize() {
        if (!selectedImage) { needPicture(); return; }

        selectedImage.removeAttribute('width');
        selectedImage.removeAttribute('height');
        selectedImage.style.width = '';
        selectedImage.style.height = '';

        afterPictureChange();
    }

    /**
     * Alignment is applied to the picture's paragraph, not to the picture.
     *
     * A floated or margin-centred image is a CSS idea; a centred paragraph is a
     * Word idea, and w:jc is what the writer already emits. Doing it this way
     * means the alignment survives the export instead of being approximated by
     * it.
     */
    function setPictureAlign(value) {
        if (!selectedImage) { needPicture(); return; }

        var scope = editableOf(selectedImage) || surface;
        var block = selectedImage.parentNode;

        while (block && block !== scope && !isBlockElement(block)) block = block.parentNode;

        if (!block || block === scope) {
            var wrapper = document.createElement('p');
            selectedImage.parentNode.insertBefore(wrapper, selectedImage);
            wrapper.appendChild(selectedImage);
            block = wrapper;
        }

        block.style.textAlign = value;
        afterPictureChange();
    }

    function deletePicture() {
        if (!selectedImage) { needPicture(); return; }

        var doomed = selectedImage;
        clearImageSelection();
        if (doomed.parentNode) doomed.parentNode.removeChild(doomed);

        afterPictureChange();
        setStatus('Picture deleted. Save to keep the change.', 'warn');
    }

    function replacePicture() {
        if (!selectedImage) { needPicture(); return; }
        if (!imageInput) return;

        replaceTarget = selectedImage;
        imageInput.click();
    }

    function setPictureAlt() {
        if (!selectedImage) { needPicture(); return; }

        var fields = pictureFields();
        var text = fields.alt ? fields.alt.value.slice(0, 512) : '';

        selectedImage.setAttribute('alt', text);
        afterPictureChange();

        setStatus(text === '' ? 'Alternative text cleared.' : 'Alternative text set.', '');
    }

    /**
     * A picture edited inside a running field has to be written back into the
     * page setup immediately, or the page preview keeps showing the old size and
     * Apply is the only thing that ever reconciles them.
     */
    function afterPictureChange() {
        var scope = selectedImage ? editableOf(selectedImage) : activeEditable;

        if (scope && scope !== surface && scope.hasAttribute('data-dms-hf')) {
            var which = scope.getAttribute('data-dms-hf');
            if (which === 'header') pageSetup.headerHtml = runningContentOf(scope);
            else if (which === 'footer') pageSetup.footerHtml = runningContentOf(scope);
        }

        markDirty();
        positionImageHandles();

        var fields = pictureFields();
        if (selectedImage) {
            var size = displayedSizeOf(selectedImage);
            if (fields.width) fields.width.value = size.width || '';
            if (fields.height) fields.height.value = size.height || '';
        }
    }

    // ---- selecting, dragging -------------------------------------------------

    root.addEventListener('mousedown', function (event) {
        if (event.target.closest('[data-dms-img-grip]')) return;    // handled below

        var img = event.target.closest ? event.target.closest('img') : null;

        if (img && editableOf(img)) {
            if (!config.readOnly) selectImage(img);
            return;
        }

        if (editableOf(event.target) || event.target.closest('[data-dms-editor-surface]')) {
            clearImageSelection();
        }
    });

    root.addEventListener('mousedown', function (event) {
        var grip = event.target.closest ? event.target.closest('[data-dms-img-grip]') : null;
        if (!grip || !selectedImage || config.readOnly) return;

        event.preventDefault();

        var img = selectedImage;
        var start = displayedSizeOf(img);
        var startX = event.clientX;
        var natural = naturalSizeOf(img);
        var ratio = natural.width > 0 && natural.height > 0 ? natural.height / natural.width : 0;

        var limit = maxPictureWidth();

        function move(moveEvent) {
            var width = Math.max(16, Math.round(start.width + (moveEvent.clientX - startX) / zoom));
            width = Math.min(limit, width);

            img.setAttribute('width', String(width));
            if (ratio > 0) img.setAttribute('height', String(Math.round(width * ratio)));

            img.style.width = '';
            img.style.height = '';
            positionImageHandles();
        }

        function up() {
            document.removeEventListener('mousemove', move);
            document.removeEventListener('mouseup', up);
            afterPictureChange();
            setStatus('Picture resized. Save to keep the change.', 'warn');
        }

        document.addEventListener('mousemove', move);
        document.addEventListener('mouseup', up);
    });

    if (canvas) canvas.addEventListener('scroll', positionImageHandles);
    window.addEventListener('scroll', positionImageHandles, true);

    if (imageInput) {
        imageInput.addEventListener('change', function () {
            var file = imageInput.files && imageInput.files[0];
            var replacing = replaceTarget;
            replaceTarget = null;
            imageInput.value = '';
            if (!file) return;

            if (file.size > config.maxImageBytes) {
                setStatus('That picture is ' + Math.round(file.size / 1024) + ' KB; the limit is ' +
                    Math.round(config.maxImageBytes / 1024) + ' KB. Resize it and try again.', 'error');
                return;
            }

            if (!/^image\/(png|jpeg|gif|webp)$/i.test(file.type)) {
                setStatus('Pictures must be PNG, JPEG, GIF or WebP.', 'error');
                return;
            }

            var reader = new FileReader();

            reader.onload = function () {
                if (replacing && replacing.isConnected) {
                    // The author replaced the picture, not its layout: the width
                    // they set is kept and the height dropped, so the new picture
                    // takes the same width at its own aspect ratio.
                    replacing.src = String(reader.result);
                    replacing.alt = file.name.replace(/\.[^.]+$/, '');
                    replacing.removeAttribute('height');

                    selectImage(replacing);
                    afterPictureChange();
                    setStatus('Picture replaced. Save to keep the change.', 'warn');
                    return;
                }

                // Embedded, not linked. A linked image would make a controlled
                // document depend on a server outside the validated system and
                // would report every reader's address to whoever runs it.
                var img = document.createElement('img');
                img.src = String(reader.result);
                img.alt = file.name.replace(/\.[^.]+$/, '');

                focusSurface();
                insertNodeAtCursor(img);

                img.addEventListener('load', function () {
                    // A photograph is thousands of pixels wide; brought in at
                    // that size it would be scaled by the export and not by the
                    // editor, and the two would show different documents.
                    var limit = maxPictureWidth();
                    if (img.naturalWidth > limit) img.setAttribute('width', String(limit));

                    selectImage(img);
                    positionImageHandles();
                });

                afterPictureChange();
                setStatus('Picture inserted. Drag its corner to resize, or use the Picture tab.', 'warn');
            };

            reader.onerror = function () { setStatus('That picture could not be read.', 'error'); };
            reader.readAsDataURL(file);
        });
    }

    // =====================================================================
    // Paste
    //
    // Pasting from Word brings mso- styles, class soup, and - the reported
    // defect - fake bullets. The server's sanitiser is the security boundary and
    // strips all of it, but if the browser holds the mess until save, the author
    // edits something that does not match what gets stored. Cleaning on the way
    // in makes the editor honest about what it will keep.
    // =====================================================================

    var PASTE_ALLOWED = {
        P: 1, DIV: 1, SPAN: 1, BR: 1, HR: 1, H1: 1, H2: 1, H3: 1, H4: 1, H5: 1, H6: 1,
        STRONG: 1, B: 1, EM: 1, I: 1, U: 1, S: 1, STRIKE: 1, SUB: 1, SUP: 1,
        UL: 1, OL: 1, LI: 1, BLOCKQUOTE: 1, PRE: 1, CODE: 1,
        TABLE: 1, THEAD: 1, TBODY: 1, TFOOT: 1, TR: 1, TD: 1, TH: 1, CAPTION: 1,
        COLGROUP: 1, COL: 1, IMG: 1, A: 1, FIGURE: 1, FIGCAPTION: 1
    };

    var PASTE_STYLES = ['text-align', 'font-weight', 'font-style', 'text-decoration', 'color',
        'background-color', 'font-family', 'font-size', 'vertical-align', 'margin-left', 'line-height'];

    var SYMBOL_FONTS = /^(symbol|wingdings( \d)?|webdings|zapfdingbats|monotype sorts|marlett)$/i;
    var BULLET_GLYPHS = /^[·•▪●◦§o\-\*]$/;
    var NUMBER_MARKER = /^\(?([0-9]{1,3}|[a-zA-Z]|[ivxIVX]{1,5})[.)]$/;

    surface.addEventListener('paste', function (event) {
        if (config.readOnly) return;

        var data = event.clipboardData;
        if (!data) return;

        event.preventDefault();

        var html = data.getData('text/html');
        var text = data.getData('text/plain');

        if (!html) { insertPlainText(text); markDirty(); return; }

        try {
            document.execCommand('insertHTML', false, cleanPastedHtml(html));
        } catch (e) {
            insertPlainText(text);
        }

        markDirty();
    });

    function insertPlainText(text) {
        if (!text) return;

        var escaped = text.split(/\r?\n/).map(function (line) {
            return '<p>' + escapeHtml(line) + '</p>';
        }).join('');

        try {
            document.execCommand('insertHTML', false, escaped || '<p><br></p>');
        } catch (e) {
            document.execCommand('insertText', false, text);
        }
    }

    function escapeHtml(value) {
        return String(value)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    /**
     * Uses DOMParser rather than innerHTML so that nothing in the fragment is
     * ever attached to the live document - an <img onerror> in a paste would
     * otherwise fire during the clean.
     */
    function cleanPastedHtml(html) {
        var parsed;
        try { parsed = new DOMParser().parseFromString(html, 'text/html'); }
        catch (e) { return escapeHtml(html); }

        var body = parsed.body;
        if (!body) return '';

        var dangerous = body.querySelectorAll('script,style,link,meta,iframe,object,embed,form,input,svg');
        for (var i = dangerous.length - 1; i >= 0; i--) dangerous[i].parentNode.removeChild(dangerous[i]);

        rememberColumnProportions(body);

        rebuildWordLists(body);
        unwrapBlockWrappers(body);
        stripSourcePageGeometry(body);

        return serializeClean(body);
    }

    /**
     * Removes the wrapper divs a paste arrives inside.
     *
     * A paste from Word or a PDF is ONE div containing the whole document. That
     * wrapper is not content - it carries no meaning the author put there - and
     * leaving it in place caused the reported defect: pagination walked the
     * surface's children, found a single block ten thousand pixels tall, and
     * could do nothing with it, so the document had no page breaks at all.
     *
     * Pagination now descends into containers regardless, so this is no longer
     * load-bearing. It is still worth doing: a document whose paragraphs are
     * top-level paragraphs behaves predictably for every other operation too -
     * indent, styles, the format painter - and it is what the author would have
     * produced by typing.
     *
     * A div that carries formatting the author can see - a border, a background,
     * an indent, an alignment - is kept, because unwrapping it would silently
     * change how the document looks.
     */
    function unwrapBlockWrappers(body) {
        var guard = 0;

        for (var pass = 0; pass < 4; pass++) {
            var wrappers = Array.prototype.filter.call(body.querySelectorAll('div'), function (div) {
                if (carriesVisibleFormatting(div)) return false;
                return Array.prototype.some.call(div.children, isBlockElement);
            });

            if (wrappers.length === 0) return;

            for (var i = 0; i < wrappers.length && guard < 4000; i++, guard++) {
                var div = wrappers[i];
                if (!div.parentNode) continue;

                while (div.firstChild) div.parentNode.insertBefore(div.firstChild, div);
                div.parentNode.removeChild(div);
            }
        }
    }

    /**
     * Records each first-row cell's declared width before the source geometry is
     * stripped, so the table's column PROPORTIONS survive even though its
     * absolute pixels do not. A five-column table where one column was clearly
     * meant to be wide should still have a wide column.
     */
    function rememberColumnProportions(body) {
        var tables = body.querySelectorAll('table');

        for (var t = 0; t < tables.length; t++) {
            var table = tables[t];
            if (table.querySelector('colgroup')) continue;

            var firstRow = table.rows && table.rows.length > 0 ? table.rows[0] : null;
            if (!firstRow) continue;

            for (var c = 0; c < firstRow.cells.length; c++) {
                var cell = firstRow.cells[c];
                var declared = (cell.style && cell.style.width) || cell.getAttribute('width') || '';
                var value = parseFloat(declared);
                if (!isNaN(value) && value > 0) cell.setAttribute('data-dms-width', String(value));
            }
        }
    }

    /**
     * Moves a picture's CSS size onto its width and height attributes.
     *
     * Only whole pixels are taken: a size given in per cent, em or cm is
     * relative to a page that is not this one, and guessing at it would put a
     * figure in the export at a size the author never chose.
     */
    function adoptCssSizeAsAttributes(img) {
        var style = img.style;
        if (!style) return;

        var width = pixelsOf(style.width) || parseInt(img.getAttribute('width') || '0', 10);
        var height = pixelsOf(style.height) || parseInt(img.getAttribute('height') || '0', 10);

        if (width > 0 && width <= 4000) img.setAttribute('width', String(width));
        if (height > 0 && height <= 4000) img.setAttribute('height', String(height));
    }

    function pixelsOf(value) {
        var match = /^\s*(\d+(?:\.\d+)?)px\s*$/i.exec(value || '');
        return match ? Math.round(parseFloat(match[1])) : 0;
    }

    function isBlockElement(element) {
        return /^(P|DIV|H[1-6]|UL|OL|TABLE|BLOCKQUOTE|PRE|FIGURE|HR)$/.test(element.tagName);
    }

    function carriesVisibleFormatting(element) {
        var style = element.style;
        if (!style) return false;

        return !!(style.borderStyle || style.borderWidth || style.border ||
            style.backgroundColor || style.marginLeft || style.textAlign ||
            style.paddingLeft || element.getAttribute('class'));
    }

    /**
     * Drops the source document's own page geometry from pasted content.
     *
     * Content copied out of another document carries widths measured for THAT
     * document's paper - "width: 900px" from a landscape page, say. Pasted into
     * a portrait A4 document it runs past the right margin and over the edge of
     * the sheet, which is the second half of what the customer reported. The
     * page geometry of the document being written is the one that applies, so
     * the source's is removed rather than reconciled.
     */
    function stripSourcePageGeometry(body) {
        // A picture sized in CSS by the source document keeps its size, moved
        // onto the width and height attributes the exporter reads, BEFORE the
        // geometry strip below removes the style it was expressed in. Without
        // this a pasted figure loses its size and comes back at the original's
        // full pixel dimensions.
        var pictures = body.querySelectorAll('img');

        for (var p = 0; p < pictures.length; p++) {
            adoptCssSizeAsAttributes(pictures[p]);
        }

        var all = body.querySelectorAll('*');

        for (var i = 0; i < all.length; i++) {
            var style = all[i].style;
            if (!style) continue;

            style.removeProperty('width');
            style.removeProperty('min-width');
            style.removeProperty('max-width');
            style.removeProperty('height');
            style.removeProperty('min-height');
            style.removeProperty('position');
            style.removeProperty('float');
            style.removeProperty('left');
            style.removeProperty('top');
        }

        // A table's column proportions ARE worth keeping, but as proportions of
        // this document's text width rather than as the other document's pixels.
        var tables = body.querySelectorAll('table');

        for (var t = 0; t < tables.length; t++) {
            var table = tables[t];
            var firstRow = table.rows && table.rows.length > 0 ? table.rows[0] : null;
            if (!firstRow || firstRow.cells.length === 0) continue;

            var widths = [];
            var total = 0;

            for (var c = 0; c < firstRow.cells.length; c++) {
                var declared = parseFloat(firstRow.cells[c].getAttribute('data-dms-width') || '0');
                widths.push(declared);
                total += declared;
            }

            if (total <= 0) continue;

            var group = document.createElement('colgroup');

            for (var w = 0; w < widths.length; w++) {
                var col = document.createElement('col');
                col.style.width = ((widths[w] / total) * 100).toFixed(3) + '%';
                group.appendChild(col);
            }

            table.insertBefore(group, table.firstChild);
        }
    }

    /**
     * Microsoft Word's fake lists.
     *
     * Copying a bulleted list out of Word does not put a list on the clipboard.
     * It puts indented paragraphs, each beginning with a literal bullet CHARACTER
     * in the Symbol font followed by seven non-breaking spaces. A browser
     * substitutes a font and it looks fine; Word does not, because U+00A0 has no
     * glyph in Symbol, so the exported document showed seven empty boxes. That
     * was the reported defect.
     *
     * Stripping the marker alone would remove the boxes but leave paragraphs that
     * only look like a list. This rebuilds them as a real tree, keeping a stack
     * of open lists so a sub-item does not split the parent - which would restart
     * a numbered list at 1, and a procedure whose steps run 1, 2, 3, 1, 2 is
     * worse than one that was never converted.
     */
    function rebuildWordLists(body) {
        var blocks = Array.prototype.slice.call(body.querySelectorAll('p,div'));
        var stack = [];

        for (var i = 0; i < blocks.length; i++) {
            var block = blocks[i];
            var marker = wordListMarker(block);

            if (!marker) { stack = []; continue; }

            while (stack.length > 0 && stack[stack.length - 1].level > marker.level) stack.pop();

            var top = stack[stack.length - 1];

            if (top && top.level === marker.level && top.ordered !== marker.ordered) {
                stack.pop();
                top = stack[stack.length - 1];
            }

            if (!top || top.level < marker.level) {
                var list = document.createElement(marker.ordered ? 'ol' : 'ul');

                if (top) {
                    var host = top.list.lastElementChild;
                    if (!host) { host = document.createElement('li'); top.list.appendChild(host); }
                    host.appendChild(list);
                } else {
                    block.parentNode.insertBefore(list, block);
                }

                stack.push({ list: list, level: marker.level, ordered: marker.ordered });
                top = stack[stack.length - 1];
            }

            var item = document.createElement('li');
            while (block.firstChild) item.appendChild(block.firstChild);

            top.list.appendChild(item);
            block.parentNode.removeChild(block);
        }
    }

    function wordListMarker(block) {
        var style = block.getAttribute('style') || '';
        var className = block.getAttribute('class') || '';
        var looksLikeList = /mso-list\s*:/i.test(style) || /MsoList/i.test(className);

        var first = block.firstElementChild;
        var markerText = null;
        var markerNode = null;

        if (first && (first.tagName === 'SPAN' || first.tagName === 'FONT')) {
            var family = (first.style && first.style.fontFamily || '')
                .split(',')[0].trim().replace(/^['"]|['"]$/g, '');
            var text = (first.textContent || '').replace(/[\s ]+/g, '');

            if (text.length > 0 && text.length <= 4 &&
                (SYMBOL_FONTS.test(family) || BULLET_GLYPHS.test(text) || NUMBER_MARKER.test(text))) {
                markerText = text;
                markerNode = first;
            }
        }

        if (!markerNode) {
            if (!looksLikeList) return null;

            var leading = (block.textContent || '').replace(/^[\s ]+/, '');
            var token = leading.split(/[\s ]/)[0];
            if (!token || !(BULLET_GLYPHS.test(token) || NUMBER_MARKER.test(token))) return null;

            stripLeadingText(block, token);
            return { ordered: NUMBER_MARKER.test(token), level: levelOf(block, style) };
        }

        markerNode.parentNode.removeChild(markerNode);
        trimLeadingSpace(block);

        return { ordered: NUMBER_MARKER.test(markerText), level: levelOf(block, style) };
    }

    function levelOf(block, style) {
        var mso = /mso-list\s*:[^;]*level(\d+)/i.exec(style);
        if (mso) return Math.max(0, parseInt(mso[1], 10) - 1);

        var left = (block.style && block.style.marginLeft) || '';
        var inches = /^([\d.]+)in$/.exec(left);
        if (inches) return Math.max(0, Math.round(parseFloat(inches[1]) / 0.5) - 1);

        return 0;
    }

    function stripLeadingText(block, token) {
        var walker = document.createTreeWalker(block, NodeFilter.SHOW_TEXT, null, false);
        var node = walker.nextNode();
        if (!node) return;

        node.nodeValue = node.nodeValue.replace(/^[\s ]*/, '');
        if (node.nodeValue.indexOf(token) === 0) node.nodeValue = node.nodeValue.slice(token.length);
        node.nodeValue = node.nodeValue.replace(/^[\s ]*/, '');
    }

    function trimLeadingSpace(block) {
        for (var guard = 0; guard < 8; guard++) {
            var first = block.firstChild;
            if (!first) return;

            if (first.nodeType === 3) {
                var trimmed = first.nodeValue.replace(/^[\s ]+/, '');
                if (trimmed === first.nodeValue) return;

                first.nodeValue = trimmed;
                if (trimmed.length > 0) return;

                block.removeChild(first);
                continue;
            }

            if (first.nodeType === 1 && (first.textContent || '').replace(/[\s ]+/g, '') === '') {
                block.removeChild(first);
                continue;
            }

            return;
        }
    }

    function serializeClean(node) {
        var out = '';

        for (var i = 0; i < node.childNodes.length; i++) {
            var child = node.childNodes[i];

            if (child.nodeType === 3) {
                out += escapeHtml(collapseNbsp(child.nodeValue));
                continue;
            }

            if (child.nodeType !== 1) continue;

            var tag = child.tagName;

            if (!PASTE_ALLOWED[tag]) { out += serializeClean(child); continue; }

            if (tag === 'COL') {
                out += '<col' + (child.style && child.style.width
                    ? ' style="width:' + escapeHtml(child.style.width) + '"' : '') + ' />';
                continue;
            }

            if (tag === 'IMG') {
                var src = child.getAttribute('src') || '';
                if (src.indexOf('data:image/') !== 0) continue;

                // The size the source document gave the picture is kept, so a
                // pasted figure arrives at the size it was rather than at
                // whatever a 4000-pixel original happens to be. The attribute
                // first; the rendered box only if the fragment is live, which it
                // is not while it is being parsed off-document.
                var pastedWidth = parseInt(child.getAttribute('width') || '0', 10) ||
                    Math.round(child.getBoundingClientRect().width);
                var pastedHeight = parseInt(child.getAttribute('height') || '0', 10) ||
                    Math.round(child.getBoundingClientRect().height);

                // Wider than the text area is scaled down here rather than in
                // the export, so the author sees the size the file will have.
                var limit = maxPictureWidth();

                if (pastedWidth > limit) {
                    if (pastedHeight > 0) pastedHeight = Math.round(pastedHeight * (limit / pastedWidth));
                    pastedWidth = limit;
                }

                out += '<img src="' + escapeHtml(src) + '" alt="' +
                    escapeHtml(child.getAttribute('alt') || '') + '"' +
                    (pastedWidth > 0 ? ' width="' + pastedWidth + '"' : '') +
                    (pastedHeight > 0 && pastedHeight <= 4000 ? ' height="' + pastedHeight + '"' : '') +
                    ' />';
                continue;
            }

            if (tag === 'A') {
                var href = child.getAttribute('href') || '';
                if (!/^https?:|^mailto:/i.test(href)) { out += serializeClean(child); continue; }
                out += '<a href="' + escapeHtml(href) + '">' + serializeClean(child) + '</a>';
                continue;
            }

            var lower = tag.toLowerCase();
            out += '<' + lower + attributesFor(child) + '>';

            if (lower !== 'br' && lower !== 'hr' && lower !== 'col') {
                out += serializeClean(child);
                out += '</' + lower + '>';
            }
        }

        return out;
    }

    /**
     * Same rule as the server's sanitiser, and deliberately identical: two or
     * more non-breaking spaces are layout padding, a single one is meaningful.
     * The replacement space is dropped when there is already one beside it, so
     * the editor does not show one gap and the exported .docx a wider one.
     */
    function collapseNbsp(value) {
        if (value.indexOf(' ') < 0) return value;

        return value.replace(/ {2,}/g, function (match, offset, whole) {
            var before = offset > 0 ? whole.charAt(offset - 1) : '';
            var after = whole.charAt(offset + match.length);
            return (before === ' ' || after === ' ') ? '' : ' ';
        });
    }

    function attributesFor(element) {
        var out = '';

        var colspan = element.getAttribute && element.getAttribute('colspan');
        if (colspan && /^\d{1,3}$/.test(colspan)) out += ' colspan="' + colspan + '"';

        var rowspan = element.getAttribute && element.getAttribute('rowspan');
        if (rowspan && /^\d{1,3}$/.test(rowspan)) out += ' rowspan="' + rowspan + '"';

        var kept = [];
        var style = element.style;

        if (style) {
            for (var i = 0; i < PASTE_STYLES.length; i++) {
                var property = PASTE_STYLES[i];
                var value = style.getPropertyValue(property);
                if (!value) continue;
                if (/url\(|expression|javascript/i.test(value)) continue;
                if (value.length > 120) continue;

                // A symbol font is dropped for the same reason the server drops
                // it: its characters do not mean what they say, and carrying it
                // through is what puts empty boxes in the exported .docx.
                if (property === 'font-family' &&
                    SYMBOL_FONTS.test(value.split(',')[0].trim().replace(/^['"]|['"]$/g, ''))) continue;

                kept.push(property + ':' + value);
            }
        }

        if (kept.length > 0) out += ' style="' + escapeHtml(kept.join(';')) + '"';
        return out;
    }

    // =====================================================================
    // Find and replace
    // =====================================================================

    function toggleFind() {
        if (!findPanel) return;

        findPanel.hidden = !findPanel.hidden;
        if (findPanel.hidden) return;

        var input = findPanel.querySelector('[data-dms-find-term]');
        if (input) input.focus();
    }

    if (findPanel) {
        findPanel.addEventListener('click', function (event) {
            var button = event.target.closest('[data-dms-find-action]');
            if (!button) return;

            var action = button.getAttribute('data-dms-find-action');
            var termField = findPanel.querySelector('[data-dms-find-term]');
            var replacementField = findPanel.querySelector('[data-dms-find-replacement]');
            var result = findPanel.querySelector('[data-dms-find-result]');
            var term = termField ? termField.value : '';

            if (action === 'close') { findPanel.hidden = true; return; }
            if (!term) { if (result) result.textContent = 'Type what to look for.'; return; }

            if (action === 'next') {
                focusSurface();
                var found = window.find ? window.find(term, false, false, true, false, true, false) : false;

                if (result) {
                    result.textContent = found ? 'Found.'
                        : (countOccurrences(term) > 0
                            ? 'No more matches below the cursor - press again to wrap.'
                            : 'Not found.');
                }
                return;
            }

            if (action === 'replace-all') {
                // Find stays available to a reader; replace does not. This is
                // the one control inside the read-only allow-list that writes.
                if (config.readOnly) {
                    if (result) result.textContent = 'Read-only — you cannot change this document.';
                    readOnlyRefusal();
                    return;
                }

                var replaced = replaceAll(term, replacementField ? replacementField.value : '');

                if (result) {
                    result.textContent = replaced === 0 ? 'Nothing to replace.'
                        : replaced + (replaced === 1 ? ' replacement made.' : ' replacements made.');
                }

                if (replaced > 0) markDirty();
            }
        });
    }

    function countOccurrences(term) {
        return (surface.innerText || '').split(term).length - 1;
    }

    /**
     * Walks text nodes and rewrites them in place. Deliberately does NOT touch
     * markup: a replace over innerHTML could match inside a tag name or a data
     * URI and destroy the document.
     */
    function replaceAll(term, replacement) {
        if (!term) return 0;

        var walker = document.createTreeWalker(surface, NodeFilter.SHOW_TEXT, null, false);
        var nodes = [];
        var node;

        while ((node = walker.nextNode())) nodes.push(node);

        var count = 0;

        for (var i = 0; i < nodes.length; i++) {
            var value = nodes[i].nodeValue;
            if (value.indexOf(term) === -1) continue;

            var parts = value.split(term);
            count += parts.length - 1;
            nodes[i].nodeValue = parts.join(replacement);
        }

        return count;
    }

    // =====================================================================
    // Commands
    // =====================================================================

    var commands = {
        save: function () { save(false); },
        'save-close': function () { save(true); },

        undo: function () { execOrExplain('undo', 'There is nothing left to undo.'); },
        redo: function () { execOrExplain('redo', 'There is nothing to redo.'); },
        'format-painter': toggleFormatPainter,

        bold: function () { exec('bold'); },
        italic: function () { exec('italic'); },
        underline: function () { exec('underline'); },
        strike: function () { exec('strikeThrough'); },
        superscript: function () { exec('superscript'); },
        subscript: function () { exec('subscript'); },
        'clear-format': function () { exec('removeFormat'); applyNamedStyle('normal'); },

        'align-left': function () { exec('justifyLeft'); },
        'align-center': function () { exec('justifyCenter'); },
        'align-right': function () { exec('justifyRight'); },
        'align-justify': function () { exec('justifyFull'); },

        'list-bullet': function () { exec('insertUnorderedList'); },
        'list-number': function () { exec('insertOrderedList'); },
        'list-legal': function () {
            exec('insertOrderedList');
            eachBlock(function (block) {
                var list = block.closest ? block.closest('ol') : null;
                if (list) list.classList.add('dms-doc-legal');
            });
        },
        indent: function () { exec('indent'); },
        outdent: function () { exec('outdent'); },

        'border-on': function () { setParagraphBorder(true); },
        'border-off': function () { setParagraphBorder(false); },

        'insert-table': insertTable,
        'insert-image': function () {
            if (config.readOnly) { readOnlyRefusal(); return; }
            replaceTarget = null;
            if (!imageInput) { setStatus('Pictures cannot be inserted on this screen.', 'warn'); return; }
            setStatus('Choose a picture file...', 'busy');
            imageInput.click();
        },

        'picture-apply-size': function () {
            var fields = pictureFields();
            setPictureSize(parseInt(fields.width ? fields.width.value : '', 10) || 0,
                parseInt(fields.height ? fields.height.value : '', 10) || 0);
        },
        'picture-reset-size': resetPictureSize,
        'picture-align-left': function () { setPictureAlign('left'); },
        'picture-align-center': function () { setPictureAlign('center'); },
        'picture-align-right': function () { setPictureAlign('right'); },
        'picture-replace': replacePicture,
        'picture-delete': deletePicture,
        'picture-alt': setPictureAlt,
        'insert-link': insertHyperlink,
        'insert-rule': function () { insertHtml('<hr /><p><br></p>'); },
        'insert-pagebreak': function () { insertHtml('<hr class="dms-doc-pagebreak" /><p><br></p>'); },
        'insert-symbol': toggleSymbolPalette,
        'insert-caption': insertCaption,
        'insert-toc': insertTableOfContents,
        'update-toc': function () {
            if (!indexBlock()) { setStatus('There is no index in this document yet.', 'warn'); return; }
            rebuildIndex(true);
            markDirty();
            reflow();
            setStatus('Index updated from the current headings.', '');
        },
        'auto-number': toggleAutoNumbering,
        'insert-date': function () {
            insertHtml(escapeHtml(new Date().toLocaleDateString('en-GB',
                { day: '2-digit', month: 'short', year: 'numeric' })));
        },

        'page-setup': openPageSetup,
        'page-setup-apply': applyPageSetup,
        'page-setup-cancel': function () {
            var dialog = root.querySelector('[data-dms-pagesetup]');
            if (dialog) dialog.hidden = true;
        },
        'edit-header': function () { enterHeaderFooter('header', hfMode ? hfPage : 1); },
        'edit-footer': function () { enterHeaderFooter('footer', hfMode ? hfPage : 1); },
        'hf-goto-header': function () { gotoRunning('header'); },
        'hf-goto-footer': function () { gotoRunning('footer'); },
        'hf-previous-page': function () { stepRunningPage(-1); },
        'hf-next-page': function () { stepRunningPage(1); },
        'hf-apply-settings': applyHeaderFooterSettings,
        'hf-remove-header': function () { removeRunning('header'); },
        'hf-remove-footer': function () { removeRunning('footer'); },
        'hf-close': exitHeaderFooter,

        'orientation-portrait': function () { setOrientation('portrait'); },
        'orientation-landscape': function () { setOrientation('landscape'); },
        'columns-1': function () { setColumns(1); },
        'columns-2': function () { setColumns(2); },
        'columns-3': function () { setColumns(3); },

        'row-above': function () { addRow(true); },
        'row-below': function () { addRow(false); },
        'column-left': function () { addColumn(true); },
        'column-right': function () { addColumn(false); },
        'delete-row': deleteRow,
        'delete-column': deleteColumn,
        'delete-table': deleteTable,
        'merge-right': function () { mergeCell('right'); },
        'merge-down': function () { mergeCell('down'); },
        'split-cell': splitCell,
        'header-row': toggleHeaderRow,
        'table-borders': toggleTableBorders,
        'column-width': setColumnWidth,
        'cell-top': function () { setCellAlign('top'); },
        'cell-middle': function () { setCellAlign('middle'); },
        'cell-bottom': function () { setCellAlign('bottom'); },

        find: toggleFind,
        'word-count': function () {
            updateCount();
            setStatus(countBox ? countBox.textContent : '', '');
        },
        'zoom-in': function () { zoom = Math.min(2, zoom + 0.1); applyZoom(); },
        'zoom-out': function () { zoom = Math.max(0.5, zoom - 0.1); applyZoom(); },
        'zoom-reset': function () { zoom = 1; applyZoom(); },
        'toggle-marks': function () { surface.classList.toggle('show-marks'); },
        'toggle-navigation': toggleNavigation
    };

    // =====================================================================
    // Ribbon
    // =====================================================================

    function selectTab(name) {
        var tabs = root.querySelectorAll('[data-dms-tab]');
        for (var i = 0; i < tabs.length; i++) {
            var active = tabs[i].getAttribute('data-dms-tab') === name;
            tabs[i].classList.toggle('is-active', active);
            tabs[i].setAttribute('aria-selected', active ? 'true' : 'false');
        }

        var panels = root.querySelectorAll('[data-dms-panel]');
        for (var j = 0; j < panels.length; j++) {
            panels[j].hidden = panels[j].getAttribute('data-dms-panel') !== name;
        }
    }

    var pictureTabWasShown = false;

    /**
     * Contextual tabs appear only when what they act on is selected, exactly as
     * Word's do: the table tabs with the caret in a table, the picture tab with
     * a picture selected.
     */
    function syncContextualTabs() {
        var available = {
            table: !!currentCell(),
            picture: !!(selectedImage && selectedImage.isConnected),
            headerfooter: !!hfMode
        };

        var contextual = root.querySelectorAll('[data-dms-contextual]');

        for (var i = 0; i < contextual.length; i++) {
            var kind = contextual[i].getAttribute('data-dms-contextual') || 'table';
            contextual[i].hidden = !available[kind];
        }

        var active = root.querySelector('[data-dms-tab].is-active');
        var activeKind = active ? active.getAttribute('data-dms-contextual') : null;

        if (activeKind && !available[activeKind]) selectTab('home');

        // Selecting a picture brings its tab forward once, the way Word's
        // Picture Format tab comes forward; it does not keep stealing the ribbon
        // back while the author works in another tab.
        if (available.picture && !pictureTabWasShown) selectTab('picture');
        pictureTabWasShown = available.picture;
    }

    root.addEventListener('click', function (event) {
        var tab = event.target.closest('[data-dms-tab]');
        if (tab && root.contains(tab)) {
            event.preventDefault();
            selectTab(tab.getAttribute('data-dms-tab'));
            return;
        }

        var symbol = event.target.closest('[data-dms-symbol]');
        if (symbol && root.contains(symbol)) {
            event.preventDefault();
            insertHtml(escapeHtml(symbol.getAttribute('data-dms-symbol')));
            return;
        }

        var button = event.target.closest('[data-dms-cmd]');
        if (!button || !root.contains(button)) return;

        event.preventDefault();
        var name = button.getAttribute('data-dms-cmd');
        if (commands[name]) commands[name]();
    });

    root.addEventListener('change', function (event) {
        var select = event.target.closest('[data-dms-select]');
        if (!select) return;

        var kind = select.getAttribute('data-dms-select');
        var value = select.value;
        if (!value) return;

        if (kind === 'style') applyNamedStyle(value);
        else if (kind === 'font') applyFontFamily(value);
        else if (kind === 'size') applyFontSize(value);
        else if (kind === 'line-spacing') setLineSpacing(value);
        else if (kind === 'space-before') setSpaceBefore(value);
        else if (kind === 'space-after') setSpaceAfter(value);
        else if (kind === 'margins') setMarginPreset(value);
        else if (kind === 'page-size') setPageSize(value);
        else if (kind === 'style-set') setStyleSet(value);

        // Word's font and size boxes keep showing what the caret is in; they do
        // not snap back to a blank placeholder the instant you use them.
        // Snapping back was itself read as "the combo box did not work", so the
        // boxes that describe a state now hold their value and are re-read from
        // the caret by refreshToolbarState(). The ones that are an ACTION
        // (spacing before/after, line spacing) still reset, because there is no
        // single current value for a multi-paragraph selection.
        if (kind === 'line-spacing' || kind === 'space-before' || kind === 'space-after') {
            select.selectedIndex = 0;
        }

        refreshToolbarState();
    });

    root.addEventListener('input', function (event) {
        var picker = event.target.closest('[data-dms-color]');
        if (!picker) return;

        var kind = picker.getAttribute('data-dms-color');

        if (kind === 'shading') setParagraphShading(picker.value);
        else if (kind === 'cell') setCellShading(picker.value);
        else exec(kind === 'highlight' ? 'hiliteColor' : 'foreColor', picker.value);
    });

    /**
     * execCommand('fontSize') only understands the seven legacy HTML sizes, none
     * of which map to points. Applying the size as CSS gives the author real
     * point sizes and gives the .docx writer a value it converts to half-points
     * exactly.
     */
    /**
     * Word's "automatically select the whole word" behaviour.
     *
     * With the caret sitting inside a word and no selection, Word applies
     * character formatting to that whole word. Requiring a selection instead -
     * which is what this editor used to do - meant an author who clicked in
     * the middle of a heading and chose 14pt was told to "select some text
     * first", which reads as the control being broken.
     *
     * Returns true when the selection now covers something to format.
     */
    function expandToWordIfCollapsed() {
        var s = selection();
        if (!s) return false;
        if (!s.isCollapsed) return true;

        var range = s.getRangeAt(0);
        var node = range.startContainer;
        if (!node || node.nodeType !== 3) return false;

        var text = node.nodeValue || '';
        var at = range.startOffset;
        var isWord = function (ch) { return ch && /[^\s ]/.test(ch); };

        var from = at;
        while (from > 0 && isWord(text.charAt(from - 1))) from--;

        var to = at;
        while (to < text.length && isWord(text.charAt(to))) to++;

        if (to <= from) return false;

        var word = document.createRange();
        word.setStart(node, from);
        word.setEnd(node, to);
        s.removeAllRanges();
        s.addRange(word);
        rememberSelection();
        return true;
    }

    /**
     * The size the next typed characters should take when the caret is not in a
     * word. Chrome's execCommand('fontSize') leaves a pending <font size="7">
     * for the next keystroke; normalisePendingFontSize turns that into the real
     * point size once the author types.
     */
    var pendingFontSize = null;

    function normaliseFontSevens(points) {
        var scope = activeEditable && activeEditable.isConnected ? activeEditable : surface;
        var marked = scope.querySelectorAll('font[size="7"]');
        if (marked.length === 0) return 0;

        // Replacing the node the caret is inside would leave the author typing
        // somewhere else, so where the caret was is noted and put back.
        var s = window.getSelection();
        var wasCollapsed = !s || !s.rangeCount || s.isCollapsed;
        var caretIn = wasCollapsed && s && s.rangeCount ? s.anchorNode : null;

        var landing = null;
        var first = null;
        var last = null;
        var changed = 0;

        for (var i = 0; i < marked.length; i++) {
            var span = document.createElement('span');
            span.style.fontSize = points + 'pt';

            var heldCaret = caretIn && marked[i].contains(caretIn);
            while (marked[i].firstChild) span.appendChild(marked[i].firstChild);
            marked[i].parentNode.replaceChild(span, marked[i]);

            if (heldCaret) landing = span;
            if (!first) first = span;
            last = span;
            changed++;
        }

        // Replacing the nodes drops the selection, and losing it is not
        // cosmetic: the author's next action - bold, a colour, another size -
        // would land on nothing and look like a second broken control. Word
        // leaves the text selected, so this does too.
        try {
            var range = document.createRange();

            if (landing) {
                range.selectNodeContents(landing);
                range.collapse(false);
            } else if (!wasCollapsed && first) {
                range.setStartBefore(first.firstChild || first);
                range.setEndAfter(last.lastChild || last);
            } else {
                return changed;
            }

            s.removeAllRanges();
            s.addRange(range);
            rememberSelection();
        } catch (e) { /* the caret is not worth an exception */ }

        return changed;
    }

    /**
     * execCommand('fontSize') only understands the seven legacy HTML sizes, none
     * of which map to points. Applying the size as CSS gives the author real
     * point sizes and gives the .docx writer a value it converts to half-points
     * exactly.
     */
    function applyFontSize(points) {
        if (config.readOnly) { readOnlyRefusal(); return; }
        focusSurface();

        var s = selection();
        if (!s) { setStatus('Put the cursor in the document first, then choose a size.', 'warn'); return; }

        var haveText = expandToWordIfCollapsed();

        try {
            // styleWithCSS is ON for the rest of the editor (bold and colour
            // must emit <span style> for the export whitelist). It has to be
            // turned OFF for exactly this call: with it on, Chrome renders
            // fontSize 7 as `font-size: xxx-large` - a keyword, not a point
            // size - so the <font size="7"> markers this function looks for
            // were never produced and EVERY size the author chose came out as
            // xxx-large. That is the font-size box "not working".
            document.execCommand('styleWithCSS', false, false);
            document.execCommand('fontSize', false, '7');
            document.execCommand('styleWithCSS', false, true);

            if (haveText) {
                if (normaliseFontSevens(points) === 0) {
                    setStatus('The font size could not be applied here.', 'warn');
                    return;
                }
                pendingFontSize = null;
                setStatus('Font size ' + points + ' pt applied.', '');
            } else {
                // Nothing selected and the caret is not in a word: this is the
                // size for what comes next, exactly as Word behaves.
                pendingFontSize = points;
                setStatus('Font size ' + points + ' pt - applies to what you type next.', '');
            }
        } catch (e) {
            setStatus('The font size could not be applied in this browser.', 'warn');
            return;
        }

        markDirty();
        refreshToolbarState();
    }

    function applyFontFamily(name) {
        if (config.readOnly) { readOnlyRefusal(); return; }
        focusSurface();

        if (!selection()) { setStatus('Put the cursor in the document first, then choose a font.', 'warn'); return; }

        // Chrome keeps a pending font for the next keystroke when the caret is
        // collapsed, so no fallback is needed here - unlike the size.
        expandToWordIfCollapsed();
        exec('fontName', name);
        setStatus(name + ' applied.', '');
    }

    function refreshToolbarState() {
        var states = {
            bold: 'bold', italic: 'italic', underline: 'underline', strike: 'strikeThrough',
            'align-left': 'justifyLeft', 'align-center': 'justifyCenter',
            'align-right': 'justifyRight', 'align-justify': 'justifyFull',
            'list-bullet': 'insertUnorderedList', 'list-number': 'insertOrderedList'
        };

        Object.keys(states).forEach(function (name) {
            var button = root.querySelector('[data-dms-cmd="' + name + '"]');
            if (!button) return;

            var active = false;
            try { active = document.queryCommandState(states[name]); } catch (e) { active = false; }

            button.classList.toggle('is-active', !!active);
            button.setAttribute('aria-pressed', active ? 'true' : 'false');
        });

        reflectFontBoxes();
    }

    /**
     * Makes the font, size and style boxes report what the caret is standing
     * in, the way Word's do. Without this the boxes were write-only: an author
     * could not tell what the current font was, and after using one it went
     * blank again, which is what "the combo boxes are not working" describes
     * even when the formatting was in fact applied.
     */
    function reflectFontBoxes() {
        var s = window.getSelection();
        var node = s && s.rangeCount ? s.anchorNode : null;
        var element = node ? (node.nodeType === 1 ? node : node.parentNode) : null;
        if (!element || !editableOf(element)) return;

        var computed = window.getComputedStyle(element);

        setSelectIfPresent('font', matchFontOption(computed.fontFamily));
        setSelectIfPresent('size', nearestSizeOption(computed.fontSize));

        var block = element.closest ? element.closest('p,h1,h2,h3,h4,h5,h6,blockquote,pre,li') : null;
        if (block) setSelectIfPresent('style', styleNameOf(block));
    }

    function setSelectIfPresent(kind, value) {
        var select = root.querySelector('select[data-dms-select="' + kind + '"]');
        if (!select || value === null) return;
        if (document.activeElement === select) return;   // do not fight the open dropdown

        for (var i = 0; i < select.options.length; i++) {
            if (select.options[i].value === value) { select.selectedIndex = i; return; }
        }
    }

    /** The option whose family name the computed font-family starts with. */
    function matchFontOption(family) {
        if (!family) return null;
        var first = family.split(',')[0].replace(/["']/g, '').trim().toLowerCase();
        var select = root.querySelector('select[data-dms-select="font"]');
        if (!select) return null;

        for (var i = 0; i < select.options.length; i++) {
            var value = select.options[i].value;
            if (!value) continue;
            if (value.split(',')[0].replace(/["']/g, '').trim().toLowerCase() === first) return value;
        }

        return null;
    }

    /** px from the browser -> the nearest point size the box offers. */
    function nearestSizeOption(fontSizePx) {
        var px = parseFloat(fontSizePx);
        if (!px || isNaN(px)) return null;

        var points = px * 72 / 96;
        var select = root.querySelector('select[data-dms-select="size"]');
        if (!select) return null;

        var best = null;
        var bestGap = 1.01;   // within about a point, or we say nothing

        for (var i = 0; i < select.options.length; i++) {
            var value = parseFloat(select.options[i].value);
            if (!value || isNaN(value)) continue;

            var gap = Math.abs(value - points);
            if (gap < bestGap) { bestGap = gap; best = select.options[i].value; }
        }

        return best;
    }

    /** Which NAMED_STYLES entry this block is, for the style box. */
    function styleNameOf(block) {
        var tag = block.tagName.toLowerCase();
        if (/^h[1-4]$/.test(tag)) return tag;
        if (tag === 'blockquote') return 'quote';
        if (tag === 'pre') return 'pre';

        if (block.classList.contains('dms-doc-title')) return 'title';
        if (block.classList.contains('dms-doc-subtitle')) return 'subtitle';
        if (block.classList.contains('dms-doc-caption')) return 'caption';
        if (block.classList.contains('dms-doc-nospacing')) return 'nospacing';

        return 'normal';
    }

    document.addEventListener('selectionchange', function () {
        if (!editableOf(document.activeElement)) return;

        rememberSelection();
        refreshToolbarState();
        syncContextualTabs();
    });

    /**
     * The format painter fires when the selection has SETTLED, not while it is
     * being made.
     *
     * <para>
     * It used to run from selectionchange, which fires on the mousedown that
     * begins a drag - so the painter was spent on the empty caret where the
     * author started selecting, and by the time they released the mouse it was
     * already off. It looked completely dead. Word paints on release, and so
     * does this.
     * </para>
     */
    function paintIfArmed(event) {
        if (!formatPainter) return;

        // Only a release inside the document paints. A release on the painter
        // button itself is the author turning it off, and mouseup arrives
        // before click - painting there would consume the painter and then let
        // the click re-arm it, which is worse than not working at all.
        if (event && !editableOf(event.target)) return;
        if (!editableOf(document.activeElement)) return;

        rememberSelection();
        applyFormatPainter();
    }

    root.addEventListener('mouseup', paintIfArmed);
    surface.addEventListener('keyup', function (event) {
        // Shift+arrow selection: paint when the shift key is released.
        if (event.key === 'Shift' || event.key === 'End' || event.key === 'Home' ||
            (event.shiftKey && event.key.indexOf('Arrow') === 0)) paintIfArmed();
    });

    // =====================================================================
    // Keyboard
    // =====================================================================

    /**
     * A selected picture behaves the way Word's does: Delete or Backspace
     * removes it, Escape lets it go, and typing anything else drops the
     * selection so the keystroke lands in the text rather than beside a
     * picture that still looks selected.
     */
    root.addEventListener('keydown', function (event) {
        if (!selectedImage || !selectedImage.isConnected || config.readOnly) return;
        if (!editableOf(event.target)) return;

        if (event.key === 'Delete' || event.key === 'Backspace') {
            event.preventDefault();
            deletePicture();
            return;
        }

        if (event.key === 'Escape') { clearImageSelection(); return; }
        if (event.key.length === 1 && !event.ctrlKey && !event.metaKey) clearImageSelection();
    });

    /** Escape leaves the header or footer, as it does in Word. */
    root.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape' || !hfMode) return;
        if (selectedImage && selectedImage.isConnected) return;   // that Escape deselects the picture

        event.preventDefault();
        exitHeaderFooter();
    });

    surface.addEventListener('keydown', function (event) {
        var meta = event.ctrlKey || event.metaKey;

        if (meta) {
            var key = event.key.toLowerCase();
            if (key === 's') { event.preventDefault(); save(false); return; }
            if (key === 'f') { event.preventDefault(); toggleFind(); return; }
            if (key === 'k') { event.preventDefault(); insertHyperlink(); return; }
            if (key === 'enter') { event.preventDefault(); commands['insert-pagebreak'](); return; }
        }

        if (event.key !== 'Tab') return;

        var cell = currentCell();
        if (!cell) return;

        event.preventDefault();
        var table = tableOf(cell);
        if (!table) return;

        var cells = Array.prototype.filter.call(table.querySelectorAll('td,th'), function (c) {
            return !c.classList.contains('dms-page-spacer-cell');
        });

        var index = cells.indexOf(cell);
        var next = event.shiftKey ? cells[index - 1] : cells[index + 1];

        if (!next) {
            if (event.shiftKey) return;
            addRow(false);

            cells = Array.prototype.filter.call(table.querySelectorAll('td,th'), function (c) {
                return !c.classList.contains('dms-page-spacer-cell');
            });

            next = cells[index + 1];
            if (!next) return;
        }

        focusCell(next);
    });

    surface.addEventListener('input', function () {
        if (reflowing) return;

        // A pending font size chosen with the caret between words materialises
        // here, as soon as there is a character to carry it.
        if (pendingFontSize !== null && normaliseFontSevens(pendingFontSize) > 0) pendingFontSize = null;

        markDirty();
        scheduleCount();
    });

    // =====================================================================
    // Saving
    // =====================================================================

    function antiforgeryToken() {
        var field = document.querySelector('input[name="__RequestVerificationToken"]');
        return field ? field.value : '';
    }

    /**
     * The body as it should be STORED: pagination spacers removed.
     *
     * Pagination is a view of the document, not part of it. A spacer that reached
     * the server would be stored in the controlled record, exported into the
     * .docx as a mysterious empty paragraph, and would move the moment anyone
     * changed the margins.
     */
    function bodyForSaving() {
        var clone = surface.cloneNode(true);

        var spacers = clone.querySelectorAll('[' + SPACER + ']');
        for (var i = spacers.length - 1; i >= 0; i--) spacers[i].parentNode.removeChild(spacers[i]);

        return clone.innerHTML;
    }

    function save(closeAfter) {
        if (config.readOnly || saving || !config.saveUrl) return;

        saving = true;
        setStatus('Saving…', 'busy');
        setToolbarBusy(true);

        var body = config.templateId
            ? {
                templateId: config.templateId,
                html: bodyForSaving(),
                authoringMode: valueOf('[data-dms-editor-mode]', 'BOTH'),
                pageSetup: pageSetup
            }
            : {
                documentId: config.documentId,
                documentVersionId: config.versionId,
                html: bodyForSaving(),
                expectedRowVersion: rowVersionField ? rowVersionField.value : '0',
                reason: null,
                pageSetup: pageSetup
            };

        fetch(config.saveUrl, {
            method: 'POST',
            credentials: 'same-origin',
            headers: {
                'Content-Type': 'application/json',
                'RequestVerificationToken': antiforgeryToken()
            },
            body: JSON.stringify(body)
        }).then(function (response) {
            return response.json().catch(function () {
                return { ok: false, message: 'The server returned an unreadable response (HTTP ' + response.status + ').' };
            });
        }).then(function (result) {
            saving = false;
            setToolbarBusy(false);

            if (!result || !result.ok) {
                // The author's text stays on screen either way, and saying so
                // matters: the instinct on a save error is to copy everything out
                // in a panic, and that is how uncontrolled copies get made.
                setStatus((result && result.message) || 'The document could not be saved.', 'error');
                return;
            }

            dirty = false;
            if (rowVersionField && result.rowVersion) rowVersionField.value = result.rowVersion;
            if (result.pageSetup) { pageSetup = result.pageSetup; reflow(); }

            lastAutosavedHtml = bodyForSaving();

            var note = result.fileName ? 'Saved as ' + result.fileName : 'Saved';
            if (result.notice) note += ' — ' + result.notice;
            setStatus(note, 'ok');

            if (closeAfter && result.returnUrl) window.location.href = result.returnUrl;
        }).catch(function () {
            saving = false;
            setToolbarBusy(false);
            setStatus('The save could not reach the server. Your text is still here - check your connection and try again.', 'error');
        });
    }

    function valueOf(selector, fallback) {
        var element = root.querySelector(selector);
        return element ? element.value : fallback;
    }

    function setToolbarBusy(busy) {
        var buttons = root.querySelectorAll('[data-dms-cmd="save"],[data-dms-cmd="save-close"]');
        for (var i = 0; i < buttons.length; i++) buttons[i].disabled = busy;
    }

    // Autosave writes the UNCOMMITTED buffer, not the record: it exists so a
    // crashed browser does not cost an afternoon. It is not audited and does not
    // change the document.
    if (config.autosaveSeconds > 0 && config.autosaveUrl && !config.readOnly) {
        autosaveTimer = setInterval(function () {
            if (saving || !dirty) return;

            var html = bodyForSaving();
            if (html === lastAutosavedHtml) return;

            fetch(config.autosaveUrl, {
                method: 'POST',
                credentials: 'same-origin',
                headers: {
                    'Content-Type': 'application/json',
                    'RequestVerificationToken': antiforgeryToken()
                },
                body: JSON.stringify({ documentVersionId: config.versionId, html: html })
            }).then(function (response) {
                if (!response.ok) return;
                lastAutosavedHtml = html;
                if (dirty) setStatus('Unsaved changes · draft kept on the server', 'warn');
            }).catch(function () {
                // A failed autosave is not worth interrupting the author for; the
                // explicit save is what they rely on and it reports properly.
            });
        }, config.autosaveSeconds * 1000);
    }

    window.addEventListener('beforeunload', function (event) {
        if (!dirty || config.readOnly) return;
        event.preventDefault();
        event.returnValue = '';
        return '';
    });

    window.addEventListener('pagehide', function () {
        if (autosaveTimer) clearInterval(autosaveTimer);
    });

    var resizeTimer = null;
    window.addEventListener('resize', function () {
        if (resizeTimer) clearTimeout(resizeTimer);
        resizeTimer = setTimeout(function () { setShellHeight(); reflow(); }, 200);
    });

    // =====================================================================
    // Start
    // =====================================================================

    /**
     * Read-only sessions disable the controls instead of ignoring them.
     *
     * <para>
     * Access permissions decide who may edit; the editor's job is to make that decision
     * visible. Leaving a live-looking ribbon over a document nobody may change
     * is how a correct access decision gets reported as a broken application.
     * The allow-list below is everything that only READS the document.
     * </para>
     */
    var READ_ONLY_ALLOWED = ['find', 'toggle-navigation', 'zoom-in', 'zoom-out', 'zoom-reset',
        'toggle-marks', 'word-count'];

    function disableEditingControls() {
        var controls = root.querySelectorAll('[data-dms-cmd],[data-dms-select],[data-dms-color],[data-dms-symbol]');

        for (var i = 0; i < controls.length; i++) {
            var control = controls[i];
            var name = control.getAttribute('data-dms-cmd');

            if (name && READ_ONLY_ALLOWED.indexOf(name) !== -1) continue;

            control.disabled = true;
            control.setAttribute('aria-disabled', 'true');
            control.classList.add('is-disabled');
            control.title = 'Read-only — you do not have rights to edit this document.';
        }
    }

    if (config.readOnly) {
        surface.setAttribute('contenteditable', 'false');
        disableEditingControls();
        setStatus('Read-only — your access rights do not permit editing this document.', 'warn');
    } else {
        surface.setAttribute('contenteditable', 'true');
        setStatus('Ready', '');
    }

    // A completely empty surface gives the caret nowhere to go in some browsers,
    // and the author clicks and nothing happens.
    if (surface.innerHTML.trim() === '') surface.appendChild(emptyParagraph());

    surface.setAttribute('data-style-set', pageSetup.styleSet || 'DEFAULT');

    setShellHeight();
    selectTab('home');
    syncContextualTabs();
    syncAutoNumberButton();
    syncHeaderFooterPanel();
    updateCount();
    refreshToolbarState();

    // An index restored with the document is rebuilt once on open so that its
    // page numbers describe THIS layout. A saved document opened on a narrower
    // screen still paginates the same way - the page is a fixed size - but a
    // document whose page setup was changed since it was saved would otherwise
    // show yesterday's page numbers.
    normaliseGeneratedContent();
    if (indexBlock()) rebuildIndex(true);

    reflow();
    applyZoom();

    lastAutosavedHtml = bodyForSaving();
})();
