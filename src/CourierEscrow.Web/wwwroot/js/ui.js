/**
 * UI Utilities: Toasts, Loaders, and Event System
 */
(function () {
	'use strict';

	/**
	 * Toast Notifications
	 */
	window.ui = window.ui || {};

	window.ui.toast = {
		container: null,
		timers: {},

		_ensureContainer: function () {
			if (!this.container) {
				this.container = document.createElement('div');
				this.container.className = 'toast-container';
				this.container.setAttribute('aria-live', 'polite');
				this.container.setAttribute('aria-atomic', 'true');
				document.body.appendChild(this.container);
			}
		},

		show: function (message, type, duration) {
			type = type || 'info';
			duration = duration || 4000;

			this._ensureContainer();

			const id = 'toast-' + Date.now() + Math.random().toString(36).substr(2, 9);
			const toast = document.createElement('div');
			toast.id = id;
			toast.className = 'toast ' + type;
			toast.setAttribute('role', 'status');

			const content = document.createElement('div');
			content.textContent = message;
			toast.appendChild(content);

			this.container.appendChild(toast);

			if (this.timers[id]) clearTimeout(this.timers[id]);

			this.timers[id] = setTimeout(() => {
				toast.classList.add('fading');
				setTimeout(() => {
					toast.remove();
					delete this.timers[id];
				}, 300);
			}, duration);

			return id;
		},

		success: function (message, duration) { return this.show(message, 'success', duration); },
		error: function (message, duration) { return this.show(message, 'error', duration || 6000); },
		info: function (message, duration) { return this.show(message, 'info', duration); },
		warn: function (message, duration) { return this.show(message, 'warn', duration); },

		dismiss: function (id) {
			if (this.timers[id]) clearTimeout(this.timers[id]);
			const el = document.getElementById(id);
			if (el) {
				el.classList.add('fading');
				setTimeout(() => {
					el.remove();
					delete this.timers[id];
				}, 300);
			}
		}
	};

	/**
	 * Loading Overlay
	 */
	window.ui.loading = {
		overlay: null,
		stack: 0,

		show: function (message) {
			if (this.stack === 0) {
				this.overlay = document.createElement('div');
				this.overlay.className = 'loading-overlay';
				const inner = document.createElement('div');
				inner.innerHTML = '<div class="spinner lg"></div><p style="margin: 1rem 0 0; color: var(--text);">' + (message || 'Loading...') + '</p>';
				this.overlay.appendChild(inner);
				document.body.appendChild(this.overlay);
			}
			this.stack++;
		},

		hide: function () {
			this.stack = Math.max(0, this.stack - 1);
			if (this.stack === 0 && this.overlay) {
				this.overlay.remove();
				this.overlay = null;
			}
		},

		hideAll: function () {
			this.stack = 0;
			if (this.overlay) {
				this.overlay.remove();
				this.overlay = null;
			}
		}
	};

	/**
	 * Event triggers and polling
	 */
	window.ui.events = {
		listeners: {},
		intervals: {},

		on: function (event, callback) {
			if (!this.listeners[event]) this.listeners[event] = [];
			this.listeners[event].push(callback);
		},

		off: function (event, callback) {
			if (!this.listeners[event]) return;
			this.listeners[event] = this.listeners[event].filter(cb => cb !== callback);
		},

		emit: function (event, data) {
			if (!this.listeners[event]) return;
			this.listeners[event].forEach(cb => {
				try { cb(data); } catch (e) { console.error('Event listener error:', e); }
			});
		},

		startPolling: function (pollKey, fn, intervalMs) {
			if (this.intervals[pollKey]) clearInterval(this.intervals[pollKey]);

			// Run immediately first
			try { fn(); } catch (e) { console.error('Poll error:', e); }

			// Then on interval
			this.intervals[pollKey] = setInterval(() => {
				try { fn(); } catch (e) { console.error('Poll error:', e); }
			}, intervalMs || 30000);
		},

		stopPolling: function (pollKey) {
			if (this.intervals[pollKey]) {
				clearInterval(this.intervals[pollKey]);
				delete this.intervals[pollKey];
			}
		},

		stopAllPolling: function () {
			Object.keys(this.intervals).forEach(key => this.stopPolling(key));
		}
	};

	/**
	 * Keyboard shortcuts
	 */
	window.ui.shortcuts = {
		handlers: {},

		register: function (key, callback) {
			if (!this.handlers[key]) this.handlers[key] = [];
			this.handlers[key].push(callback);
		},

		init: function () {
			document.addEventListener('keydown', (e) => {
				const key = (e.ctrlKey || e.metaKey ? 'ctrl+' : '') + (e.shiftKey ? 'shift+' : '') + e.key.toLowerCase();
				if (this.handlers[key]) {
					e.preventDefault();
					this.handlers[key].forEach(cb => cb(e));
				}
			});
		}
	};

	// Initialize shortcuts
	window.ui.shortcuts.init();

	/**
	 * Debounce utility
	 */
	window.ui.debounce = function (fn, delay) {
		let timeout;
		return function (...args) {
			clearTimeout(timeout);
			timeout = setTimeout(() => fn.apply(this, args), delay);
		};
	};

	/**
	 * Throttle utility
	 */
	window.ui.throttle = function (fn, limit) {
		let inThrottle;
		return function (...args) {
			if (!inThrottle) {
				fn.apply(this, args);
				inThrottle = true;
				setTimeout(() => inThrottle = false, limit);
			}
		};
	};

})();
