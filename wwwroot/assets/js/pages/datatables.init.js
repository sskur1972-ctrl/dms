/*
Template Name: Velzon - Admin & Dashboard Template
Author: Themesbrand
Website: https://Themesbrand.com/
Contact: Themesbrand@gmail.com
File: datatables init js
*/

document.addEventListener('DOMContentLoaded', function () {
    let table = new DataTable('#example',);
});


document.addEventListener('DOMContentLoaded', function () {
  let table = new DataTable('#scroll-vertical', {
      "scrollY":        "210px",
      "scrollCollapse": true,
      "paging":         false
    });
    
});

document.addEventListener('DOMContentLoaded', function () {
  let table = new DataTable('#scroll-horizontal', {
      "scrollX": true
    });
});

document.addEventListener('DOMContentLoaded', function () {
  let table = new DataTable('#alternative-pagination', {
      "pagingType": "full_numbers"
    });
});

//fixed header
document.addEventListener('DOMContentLoaded', function () {
  let table = new DataTable('#fixed-header', {
      "fixedHeader": true
    });
    
}); 


//buttons exmples
document.addEventListener('DOMContentLoaded', function () {
  let table = new DataTable('#buttons-datatables', {
        dom: 'Bfrtip',
        buttons: [
            'copy', 'csv', 'excel', 'print'
        ]
    });
}); 

//buttons exmples
document.addEventListener('DOMContentLoaded', function () {
  let table = new DataTable('#ajax-datatables', {
        "ajax": 'assets/json/datatable.json'
    });
}); 
