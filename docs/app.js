"use strict";
(() => {
  const shots = {
    home: { file: "home.png", caption: "游戏首页 · 选择启动平台，记录每一次回到农场的日子。", alt: "游戏首页：选择 Steam、Xbox 或本地离线游戏，查看启动日历与游玩时长" },
    mods: { file: "mods.png", caption: "Mod 管理 · 导入、搜索、启用或禁用，检查依赖与重复模组。", alt: "Mod 管理界面：模组列表、启用禁用操作和依赖检查" },
    downloads: { file: "downloads.png", caption: "社区下载 · 浏览国内社区资源，公开 ZIP 可下载并安装到游戏。", alt: "社区资源下载界面：国内资源、作者资源与下载入口" },
    logs: { file: "logs.png", caption: "日志与 AI · 自选厂商与模型，先筛选证据，再确认发送分析。", alt: "日志与 AI 界面：厂商连接、模型、API 密钥及日志筛选设置" },
    guides: { file: "guides.png", caption: "中文攻略 · 在软件内搜索 BWIKI，阅读图文并收藏常用条目。", alt: "BWIKI 中文攻略界面：搜索、攻略列表、收藏与阅读入口" },
    multiplayer: { file: "multiplayer.png", caption: "好友联机 · 基于 EasyTier，创建房间、分享星芽邀请码。", alt: "联机界面：EasyTier 核心安装、房间状态、创建与加入房间" },
    appearance: { file: "appearance.png", caption: "自定义外观 · 选择主题与强调色，换上喜欢的背景图片。", alt: "外观设置界面：主题、强调色与自定义背景配置" }
  };
  const galleryImage = document.getElementById("gallery-image");
  const caption = document.getElementById("gallery-caption");
  function selectShot(key) {
    const shot = shots[key];
    if (!shot) return;
    galleryImage.src = `./assets/${shot.file}`;
    galleryImage.alt = shot.alt;
    caption.textContent = shot.caption;
    document.querySelectorAll("[data-gallery]").forEach(button => button.setAttribute("aria-pressed", String(button.dataset.gallery === key)));
  }
  document.querySelectorAll("[data-gallery]").forEach(button => button.addEventListener("click", () => selectShot(button.dataset.gallery)));
  document.querySelectorAll("[data-show-gallery]").forEach(button => button.addEventListener("click", () => {
    selectShot(button.dataset.showGallery);
    document.getElementById("screenshots").scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "instant" : "smooth" });
  }));

  const menu = document.getElementById("menu-toggle");
  const navigation = document.getElementById("navigation");
  function closeMenu() { menu.setAttribute("aria-expanded", "false"); navigation.classList.remove("open"); }
  menu.addEventListener("click", () => {
    const open = menu.getAttribute("aria-expanded") !== "true";
    menu.setAttribute("aria-expanded", String(open));
    menu.setAttribute("aria-label", open ? "关闭导航" : "打开导航");
    navigation.classList.toggle("open", open);
  });
  navigation.querySelectorAll("a").forEach(link => link.addEventListener("click", closeMenu));
  document.addEventListener("keydown", event => { if (event.key === "Escape") closeMenu(); });
  document.addEventListener("click", event => { if (!event.target.closest(".site-header")) closeMenu(); });
  matchMedia("(min-width: 581px)").addEventListener("change", closeMenu);

  const lightbox = document.getElementById("lightbox");
  const lightboxImage = document.getElementById("lightbox-image");
  let opener;
  document.querySelectorAll("[data-lightbox]").forEach(button => button.addEventListener("click", () => {
    const shot = button.querySelector("img");
    opener = button;
    lightboxImage.src = shot.currentSrc || shot.src;
    lightboxImage.alt = shot.alt;
    document.getElementById("lightbox-caption").textContent = shot.alt;
    lightbox.showModal();
    document.getElementById("lightbox-close").focus();
  }));
  document.getElementById("lightbox-close").addEventListener("click", () => lightbox.close());
  lightbox.addEventListener("click", event => { if (event.target === lightbox) { const box = lightbox.getBoundingClientRect(); if (event.clientX < box.left || event.clientX > box.right || event.clientY < box.top || event.clientY > box.bottom) lightbox.close(); } });
  lightbox.addEventListener("close", () => opener?.focus({ preventScroll: true }));

  const status = document.getElementById("copy-status");
  let statusTimer;
  document.querySelectorAll("[data-copy]").forEach(button => button.addEventListener("click", async () => {
    const code = button.dataset.copy;
    try {
      if (navigator.clipboard?.writeText) await navigator.clipboard.writeText(code);
      else {
        const input = document.createElement("textarea");
        input.value = code; input.setAttribute("readonly", ""); input.style.cssText = "position:fixed;left:-9999px;top:0";
        document.body.append(input); input.select();
        const success = document.execCommand("copy"); input.remove(); button.focus({ preventScroll: true });
        if (!success) throw new Error("Copy unavailable");
      }
      status.textContent = `已复制提取码：${code}`;
    } catch { status.textContent = `请手动复制提取码：${code}`; }
    status.classList.add("visible"); clearTimeout(statusTimer);
    statusTimer = setTimeout(() => status.classList.remove("visible"), 3500);
  }));
})();
