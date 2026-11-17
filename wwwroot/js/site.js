document.addEventListener('DOMContentLoaded', function () {
    var toggle = document.getElementById('qamsSidebarToggle');
    var sidebar = document.querySelector('.qams-sidebar');
    var backdrop = document.getElementById('qamsSidebarBackdrop');
    function closeSidebar() {
        if (sidebar) sidebar.classList.remove('show');
        if (backdrop) backdrop.classList.remove('show');
    }
    if (toggle && sidebar) {
        toggle.addEventListener('click', function () {
            sidebar.classList.toggle('show');
            if (backdrop) backdrop.classList.toggle('show', sidebar.classList.contains('show'));
        });
    }
    if (backdrop) backdrop.addEventListener('click', closeSidebar);
    document.querySelectorAll('.qams-sidebar .nav-link').forEach(function (link) {
        link.addEventListener('click', function () { if (window.innerWidth < 992) closeSidebar(); });
    });

    document.querySelectorAll('table.qams-datatable').forEach(function (t) {
        if (window.jQuery && window.jQuery.fn.DataTable) {
            var dt = window.jQuery(t).DataTable({
                order: [], pageLength: 10, lengthChange: true,
                language: { search: '', searchPlaceholder: 'Search records...', lengthMenu: 'Show _MENU_', emptyTable: 'No records available' }
            });

            // Stop the browser from auto-filling the saved login user name into the table search box.
            var box = window.jQuery(dt.table().container()).find('.dataTables_filter input')[0];
            if (box) {
                box.setAttribute('autocomplete', 'off');
                box.setAttribute('name', 'qams-table-filter-' + Math.random().toString(36).slice(2));
                box.setAttribute('data-lpignore', 'true');
                box.readOnly = true;                                   // browsers do not autofill read-only fields
                box.addEventListener('focus', function () { box.readOnly = false; });
                // If an autofill still slipped in before the user typed anything, clear it.
                setTimeout(function () {
                    if (document.activeElement !== box && box.value) { box.value = ''; dt.search('').draw(); }
                }, 300);
            }
        }
    });

    setTimeout(function () {
        document.querySelectorAll('.alert-dismissible').forEach(function (a) {
            if (window.bootstrap) { new bootstrap.Alert(a).close(); }
        });
    }, 6000);

    document.querySelectorAll('.password-toggle').forEach(function (button) {
        button.addEventListener('click', function () {
            var input = document.getElementById(button.dataset.target);
            if (!input) return;
            var reveal = input.type === 'password';
            input.type = reveal ? 'text' : 'password';
            button.innerHTML = '<i class="bi bi-eye' + (reveal ? '-slash' : '') + '"></i>';
            button.setAttribute('aria-label', reveal ? 'Hide password' : 'Show password');
        });
    });
    document.querySelectorAll('.module-select-all').forEach(function (button) {
        button.addEventListener('click', function () {
            var boxes = Array.from(button.closest('.role-permission-group').querySelectorAll('.permission-checkbox'));
            var shouldCheck = boxes.some(function (box) { return !box.checked; });
            boxes.forEach(function (box) { box.checked = shouldCheck; });
            button.textContent = shouldCheck ? 'Clear all' : 'Select all';
        });
    });
    var roleSelectAll = document.querySelector('.role-select-all');
    if (roleSelectAll) {
        roleSelectAll.addEventListener('click', function () {
            var boxes = Array.from(document.querySelectorAll('.permission-checkbox'));
            var shouldCheck = boxes.some(function (box) { return !box.checked; });
            boxes.forEach(function (box) { box.checked = shouldCheck; });
            roleSelectAll.textContent = shouldCheck ? 'Clear all permissions' : 'Select all permissions';
        });
    }

    // UI layer for regulated decisions. The global server filter independently enforces it.
    var esignDialog = document.getElementById('qamsESignDialog');
    var esignPassword = document.getElementById('qamsESignPassword');
    var esignConfirm = document.getElementById('qamsESignConfirm');
    var esignStatement = document.getElementById('qamsESignStatement');
    var esignError = document.getElementById('qamsESignError');
    var esignSubmit = document.getElementById('qamsESignSubmit');
    var esignCancel = document.getElementById('qamsESignCancel');
    var pendingESignForm = null;
    // The e-signature dialog lives outside any Bootstrap modal. When it opens on top of one (e.g. the
    // "Accept Action Plan" modal), Bootstrap's modal focus-trap pulls focus back into the modal, so typed
    // characters (the password) landed in the modal's comment box. Stop the focus events of the e-sign
    // dialog from reaching Bootstrap's document-level focus-trap listener.
    if (esignDialog) {
        ['focusin', 'focusout'].forEach(function (evt) {
            esignDialog.addEventListener(evt, function (e) { e.stopPropagation(); });
        });
    }
    var pendingESignSubmitter = null;

    function requiresESignature(action) {
        return /accept|approve|reject|rollback/i.test(action || '');
    }

    function setHidden(form, name, value) {
        var input = form.querySelector('input[type="hidden"][name="' + name + '"]');
        if (!input) {
            input = document.createElement('input');
            input.type = 'hidden';
            input.name = name;
            form.appendChild(input);
        }
        input.value = value;
    }

    function closeESignDialog() {
        if (!esignDialog) return;
        esignDialog.hidden = true;
        document.body.classList.remove('qams-esign-open');
        if (esignPassword) esignPassword.value = '';
        if (esignConfirm) esignConfirm.checked = false;
        if (esignError) esignError.textContent = '';
        pendingESignForm = null;
        pendingESignSubmitter = null;
    }

    document.addEventListener('submit', function (event) {
        var form = event.target;
        if (!(form instanceof HTMLFormElement) || !esignDialog) return;
        if (form.dataset.esignVerified === 'true') {
            delete form.dataset.esignVerified;
            return;
        }

        var submitter = event.submitter || null;
        var action = submitter && submitter.name.toLowerCase() === 'action'
            ? submitter.value
            : (form.querySelector('[name="Action"]') || {}).value;
        if (!requiresESignature(action)) return;

        event.preventDefault();
        pendingESignForm = form;
        pendingESignSubmitter = submitter;
        esignStatement.textContent = 'I confirm my identity and intend to ' + action.toLowerCase() + ' this record.';
        esignDialog.hidden = false;
        document.body.classList.add('qams-esign-open');
        window.setTimeout(function () { esignPassword.focus(); }, 50);
    }, true);

    if (esignSubmit) {
        esignSubmit.addEventListener('click', function () {
            if (!pendingESignForm) return;
            if (!esignPassword.value) {
                esignError.textContent = 'Enter your current login password.';
                esignPassword.focus();
                return;
            }
            if (!esignConfirm.checked) {
                esignError.textContent = 'Confirm the electronic-signature statement to continue.';
                esignConfirm.focus();
                return;
            }

            var form = pendingESignForm;
            var submitter = pendingESignSubmitter;
            setHidden(form, 'ESignaturePassword', esignPassword.value);
            setHidden(form, 'ESignatureConfirmed', 'true');
            form.dataset.esignVerified = 'true';
            esignDialog.hidden = true;
            document.body.classList.remove('qams-esign-open');
            if (submitter) form.requestSubmit(submitter); else form.requestSubmit();
        });
    }

    if (esignCancel) esignCancel.addEventListener('click', closeESignDialog);
    if (esignDialog) {
        esignDialog.addEventListener('click', function (event) {
            if (event.target === esignDialog) closeESignDialog();
        });
    }
    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && esignDialog && !esignDialog.hidden) closeESignDialog();
        if (event.key === 'Enter' && esignDialog && !esignDialog.hidden && document.activeElement === esignPassword) esignSubmit.click();
    });
});
