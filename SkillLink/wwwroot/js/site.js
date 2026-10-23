(function () {
  'use strict';

  // ---- Selecting several providers (customer search page) ----
  var bar = document.getElementById('select-bar');
  if (bar) {
    var boxes = Array.prototype.slice.call(document.querySelectorAll('input[name="ids"]'));
    var count = bar.querySelector('[data-count]');
    var update = function () {
      var n = boxes.filter(function (b) { return b.checked; }).length;
      count.textContent = n;
      bar.classList.toggle('show', n > 0);
      boxes.forEach(function (b) { if (!b.checked) b.disabled = n >= 5; });
    };
    boxes.forEach(function (b) { b.addEventListener('change', update); });
    update();
  }

  // ---- List / Map toggle ----
  var map = null;
  var list = document.getElementById('view-list');
  var mapView = document.getElementById('view-map');
  var toggles = document.querySelectorAll('[data-view]');

  function showMap() {
    var el = document.getElementById('map');
    var raw = document.getElementById('map-data');
    if (!el || !raw) return;
    if (!window.L) { el.innerHTML = '<div class="map-fallback">The map needs an internet connection to load. Use the list view for now.</div>'; return; }
    if (map) { map.invalidateSize(); return; }
    var data = JSON.parse(raw.textContent || '[]');
    map = L.map(el, { scrollWheelZoom: false }).setView([-22.3, 24.7], 6);
    L.tileLayer('https://tile.openstreetmap.org/{z}/{x}/{y}.png', { maxZoom: 18, attribution: '&copy; OpenStreetMap contributors' }).addTo(map);
    var points = [];
    data.forEach(function (d) {
      var r = 12 + Math.min(d.providers.length, 6) * 3;
      var m = L.circleMarker([d.lat, d.lng], { radius: r, color: '#0B2545', weight: 2, fillColor: '#F5A623', fillOpacity: 0.92 }).addTo(map);
      m.bindTooltip(String(d.providers.length), { permanent: true, direction: 'center', className: 'map-count' });
      var box = document.createElement('div'); box.className = 'map-pop';
      var h = document.createElement('strong'); h.textContent = d.name + ' (' + d.providers.length + ')'; box.appendChild(h);
      d.providers.forEach(function (p) {
        var a = document.createElement('a'); a.href = '/Home/Details/' + p.id;
        a.textContent = p.name + (p.rating > 0 ? '  ' + p.rating.toFixed(1) + ' stars' : '');
        box.appendChild(a);
        if (p.skills) { var s = document.createElement('small'); s.textContent = p.skills; box.appendChild(s); }
      });
      m.bindPopup(box);
      points.push([d.lat, d.lng]);
    });
    if (points.length > 1) map.fitBounds(points, { padding: [50, 50], maxZoom: 9 });
    else if (points.length === 1) map.setView(points[0], 8);
  }

  toggles.forEach(function (btn) {
    btn.addEventListener('click', function () {
      var v = btn.getAttribute('data-view');
      toggles.forEach(function (b) { b.classList.toggle('on', b === btn); });
      if (list) list.hidden = v !== 'list';
      if (mapView) mapView.hidden = v !== 'map';
      if (v === 'map') showMap();
    });
  });

  // ---- Photo upload: friendly size check before sending (server checks again) ----
  document.querySelectorAll('input[type=file][data-max]').forEach(function (inp) {
    inp.addEventListener('change', function () {
      var max = parseInt(inp.getAttribute('data-max'), 10);
      if (inp.files && inp.files[0] && inp.files[0].size > max) {
        alert('That photo is larger than ' + Math.round(max / 1048576) + ' MB. Please choose a smaller one.');
        inp.value = '';
      }
    });
  });
})();
