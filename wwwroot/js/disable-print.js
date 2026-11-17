(function () {
    'use strict';

    document.addEventListener('keydown', function (event) {

        // Ctrl + P
        if (event.ctrlKey && event.key.toLowerCase() === 'p') {
            event.preventDefault();
            event.stopPropagation();

            showPrintRestrictionModal()

            return false;
        }

        // Ctrl + Shift + P
        if (event.ctrlKey && event.shiftKey && event.key.toLowerCase() === 'p') {
            event.preventDefault();
            event.stopPropagation();

            showPrintRestrictionModal()

            return false;
        }
    }, true);

    function showPrintRestrictionModal() {
        var modalElement = document.getElementById('printRestrictionModal');
        if (modalElement) {
            var modal = bootstrap.Modal.getOrCreateInstance(modalElement); modal.show();
        }
    }

  

})();