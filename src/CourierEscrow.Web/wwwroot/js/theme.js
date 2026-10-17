// Light / dark for the ops console.
// <html data-theme="light|dark"> is an explicit choice and beats the device setting; with no choice the device decides.
// Applied immediately (this script is in <head>), so the page never flashes the wrong colours.
(function () {
	var KEY = 'theme';
	var root = document.documentElement;
	var media = window.matchMedia ? window.matchMedia('(prefers-color-scheme: dark)') : { matches: false };

	function saved() { try { var t = localStorage.getItem(KEY); return t === 'dark' || t === 'light' ? t : null; } catch (e) { return null; } }
	function isDark() { var t = root.getAttribute('data-theme'); return t ? t === 'dark' : !!media.matches; }

	window.themeInterop = {
		getTheme: function () { return saved(); },
		setTheme: function (theme) {
			root.setAttribute('data-theme', theme === 'dark' ? 'dark' : 'light');
			try { localStorage.setItem(KEY, theme === 'dark' ? 'dark' : 'light'); } catch (e) { /* private mode: still applies to this visit */ }
			var meta = document.querySelector('meta[name="theme-color"]');
			if (meta) meta.setAttribute('content', theme === 'dark' ? '#0f1311' : '#0f6b46');
		},
		toggleTheme: function () { var next = isDark() ? 'light' : 'dark'; this.setTheme(next); return next; },
		isDark: isDark,
		initTheme: function () { var t = saved(); if (t) root.setAttribute('data-theme', t); }
	};
	window.theme = window.themeInterop;
	window.themeInterop.initTheme();
})();
