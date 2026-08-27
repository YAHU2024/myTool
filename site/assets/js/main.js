/* ==========================================================================
   QuickTranslate GitHub Pages 站点 — 交互脚本
   - 滚动渐显（IntersectionObserver，尊重 prefers-reduced-motion）
   - GitHub Stars / Downloads 统计 CountUp（shields.io JSON）
   - 功能展示图片放大（Lightbox，含键盘支持）
   ========================================================================== */
(function () {
  "use strict";

  var prefersReducedMotion = window.matchMedia(
    "(prefers-reduced-motion: reduce)"
  ).matches;

  /* ---------- 滚动渐显 ---------- */
  function initReveal() {
    var els = document.querySelectorAll(".reveal");
    if (els.length === 0) return;

    // 无 IO 支持或用户偏好减少动效时：直接显示全部内容
    if (prefersReducedMotion || !("IntersectionObserver" in window)) return;

    document.body.classList.add("js-anim");

    // 同一网格内的元素按索引做少量错峰（0-300ms）
    els.forEach(function (el, i) {
      var delay = Math.min(i % 6, 5) * 60;
      el.style.setProperty("--reveal-delay", delay + "ms");
    });

    var io = new IntersectionObserver(
      function (entries) {
        entries.forEach(function (entry) {
          if (entry.isIntersecting) {
            entry.target.classList.add("revealed");
            io.unobserve(entry.target);
          }
        });
      },
      { threshold: 0.12, rootMargin: "0px 0px -40px 0px" }
    );

    els.forEach(function (el) {
      io.observe(el);
    });
  }

  /* ---------- 数字格式化 ---------- */
  function parseCount(str) {
    var m = String(str).trim().match(/^([\d.]+)([kKmM]?)$/);
    if (!m) return NaN;
    var mult =
      m[2].toLowerCase() === "k"
        ? 1000
        : m[2].toLowerCase() === "m"
          ? 1000000
          : 1;
    return parseFloat(m[1]) * mult;
  }

  function formatCount(n) {
    if (n >= 1000000) {
      return (n / 1000000).toFixed(1).replace(/\.0$/, "") + "M";
    }
    if (n >= 1000) {
      return (n / 1000).toFixed(1).replace(/\.0$/, "") + "k";
    }
    return String(n);
  }

  function animateCount(node, target, duration) {
    var start = null;
    duration = duration || 800;
    function tick(now) {
      if (start === null) start = now;
      var p = Math.min(1, (now - start) / duration);
      var eased = 1 - Math.pow(1 - p, 3); // cubic ease-out
      node.textContent = formatCount(Math.round(target * eased));
      if (p < 1) requestAnimationFrame(tick);
    }
    requestAnimationFrame(tick);
  }

  /* ---------- Stars / Downloads CountUp ---------- */
  function initStats() {
    var stats = document.querySelectorAll("#hero-stats .stat[data-stat]");
    if (stats.length === 0) return;

    stats.forEach(function (el) {
      var kind = el.dataset.stat;
      var url =
        kind === "stars"
          ? "https://img.shields.io/github/stars/YAHU2024/myTool.json"
          : "https://img.shields.io/github/downloads/YAHU2024/myTool/total.json";
      var target = el.querySelector("b");

      fetch(url)
        .then(function (res) {
          if (!res.ok) throw new Error("HTTP " + res.status);
          return res.json();
        })
        .then(function (data) {
          var value = parseCount(data.value);
          if (isNaN(value)) throw new Error("unparseable value");
          if (prefersReducedMotion) {
            target.textContent = formatCount(value);
          } else {
            animateCount(target, value);
          }
        })
        .catch(function () {
          // 统计不可用时隐藏该块，不影响页面其余内容
          el.style.display = "none";
        });
    });
  }

  /* ---------- 图片放大 ---------- */
  function initLightbox() {
    var lb = document.getElementById("lightbox");
    if (!lb) return;

    var img = document.getElementById("lightbox-img");
    var caption = document.getElementById("lightbox-caption");
    var closeBtn = lb.querySelector(".lightbox-close");
    var items = document.querySelectorAll(".showcase-item");

    function open(item) {
      var source = item.querySelector("img");
      img.src = source.currentSrc || source.src;
      img.alt = source.alt;
      caption.textContent = item.dataset.caption || source.alt;
      lb.hidden = false;
      document.body.style.overflow = "hidden";
      closeBtn.focus();
    }

    function close() {
      lb.hidden = true;
      document.body.style.overflow = "";
      img.removeAttribute("src");
    }

    items.forEach(function (item) {
      var source = item.querySelector("img");
      item.setAttribute("tabindex", "0");
      item.setAttribute("role", "button");
      item.setAttribute("aria-label", "放大查看：" + (source.alt || ""));

      item.addEventListener("click", function () {
        open(item);
      });
      item.addEventListener("keydown", function (e) {
        if (e.key === "Enter" || e.key === " ") {
          e.preventDefault();
          open(item);
        }
      });
    });

    closeBtn.addEventListener("click", close);
    lb.addEventListener("click", function (e) {
      if (e.target === lb) close();
    });
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && !lb.hidden) close();
    });
  }

  /* ---------- 返回顶部 ---------- */
  function initScrollTop() {
    var links = document.querySelectorAll("a[data-scroll-top]");
    if (links.length === 0) return;

    links.forEach(function (link) {
      link.addEventListener("click", function (e) {
        e.preventDefault();
        window.scrollTo({
          top: 0,
          left: 0,
          behavior: prefersReducedMotion ? "auto" : "smooth"
        });
      });
    });
  }

  /* ---------- 导航 ScrollSpy ---------- */
  function initScrollSpy() {
    var navLinks = Array.prototype.slice.call(
      document.querySelectorAll(".site-nav a[href^='#']")
    );
    if (navLinks.length === 0) return;

    var map = {};
    navLinks.forEach(function (link) {
      var id = link.getAttribute("href").slice(1);
      var section = document.getElementById(id);
      if (section) map[id] = { link: link, section: section };
    });
    var ids = Object.keys(map);
    if (ids.length === 0) return;

    var current = null;
    var rafId = null;

    // 状态未变化不重复写 DOM，避免切换抖动
    function setActive(id) {
      if (id === current) return;
      current = id;
      ids.forEach(function (i) {
        var on = i === id;
        map[i].link.classList.toggle("active", on);
        if (on) {
          map[i].link.setAttribute("aria-current", "true");
        } else {
          map[i].link.removeAttribute("aria-current");
        }
      });
    }

    // 实时几何计算：活动带（视口 45%~50%）内最靠上的 section。
    // 每次全量重算 6 个 section，结果是确定性的单一值，无 IO 回调竞态。
    function update() {
      var bandTop = window.innerHeight * 0.45;
      var bandBottom = window.innerHeight * 0.5;
      var top = null;
      ids.forEach(function (id) {
        var rect = map[id].section.getBoundingClientRect();
        if (rect.top < bandBottom && rect.bottom > bandTop) {
          if (
            top === null ||
            rect.top < map[top].section.getBoundingClientRect().top
          ) {
            top = id;
          }
        }
      });
      setActive(top);
    }

    // rAF 节流：一帧最多重算一次
    function schedule() {
      if (rafId !== null) return;
      rafId = requestAnimationFrame(function () {
        rafId = null;
        update();
      });
    }

    window.addEventListener("scroll", schedule, { passive: true });
    window.addEventListener("resize", schedule, { passive: true });
    update(); // 初始状态
  }

  /* ---------- 启动 ---------- */
  function ready(fn) {
    if (document.readyState !== "loading") {
      fn();
    } else {
      document.addEventListener("DOMContentLoaded", fn);
    }
  }

  ready(function () {
    initReveal();
    initStats();
    initLightbox();
    initScrollTop();
    initScrollSpy();
  });
})();
