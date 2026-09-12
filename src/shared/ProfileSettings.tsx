import { useCallback, useEffect, useRef, useState, type FormEvent } from "react";
import { api, type AvatarChange, type Profile } from "./api";
import { Field, Modal, UserAvatar } from "./ui";
import Icon from "../components/Icon";
import "./profile.css";

export function ProfileSettings({ profile, saved, close }: { profile: Profile; saved: (profile: Profile) => void; close: () => void }) {
  const [tab, setTab] = useState<"profile" | "password">("profile");
  const [avatar, setAvatar] = useState<AvatarChange>(() => ({ color: profile.avatar?.color ?? "#6C82AD", text: profile.avatar?.text ?? "" }));
  const [busy, setBusy] = useState(false); const [avatarLoading, setAvatarLoading] = useState(false); const [error, setError] = useState(""); const [notice, setNotice] = useState("");
  const changeAvatar = useCallback((next: AvatarChange) => { setAvatar(next); setNotice(""); }, []);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const form = event.currentTarget; const data = new FormData(form); setBusy(true); setError(""); setNotice("");
    try {
      if (tab === "profile") {
        const next = await api<Profile>("profile", "PATCH", { publicId: data.get("publicId"), displayName: data.get("displayName"), avatar }); saved(next);
        setAvatar({ color: next.avatar?.color ?? "#6C82AD", text: next.avatar?.text ?? "" }); setNotice("个人资料已保存");
      } else {
        if (data.get("password") !== data.get("confirm")) throw new Error("两次输入的新密码不一致");
        saved(await api<Profile>("profile/password", "POST", { currentPassword: data.get("currentPassword"), password: data.get("password") }));
        form.reset(); setNotice("密码已更新，其他设备需要重新登录");
      }
    } catch (error) { setError(error instanceof Error ? error.message : "无法保存，请重试"); } finally { setBusy(false); }
  };
  return <Modal title="个人设置" description="让伙伴通过你的头像、名字和 ID 认出你。" close={close}>
    <div className="shared-settings-tabs" role="tablist" aria-label="个人设置分类">
      <button role="tab" disabled={busy} aria-selected={tab === "profile"} onClick={() => { setTab("profile"); setError(""); setNotice(""); }}>头像与资料</button>
      <button role="tab" disabled={busy} aria-selected={tab === "password"} onClick={() => { setTab("password"); setError(""); setNotice(""); }}>修改密码</button>
    </div>
    <form onSubmit={event => void submit(event)}>
      {tab === "profile" ? <><AvatarDesigner profile={profile} value={avatar} changed={changeAvatar} loading={setAvatarLoading} />
        <Field label="你的名字"><input name="displayName" defaultValue={profile.displayName} required maxLength={40} autoComplete="name" /></Field>
        <Field label="个人 ID" hint="伙伴用这个 ID 将你加入工作区"><div className="shared-input-prefix"><span>@</span><input name="publicId" defaultValue={profile.publicId ?? ""} required pattern={"[a-zA-Z0-9][a-zA-Z0-9_\\-]{2,31}"} maxLength={32} /></div></Field>
      </> : <><div className="shared-settings-account">登录账号 <strong>{profile.username}</strong></div>
        <Field label="当前密码"><input name="currentPassword" type="password" required autoComplete="current-password" maxLength={1024} /></Field>
        <Field label="新密码" hint="至少 6 个字符"><input name="password" type="password" required autoComplete="new-password" minLength={6} maxLength={1024} /></Field>
        <Field label="确认新密码"><input name="confirm" type="password" required autoComplete="new-password" minLength={6} maxLength={1024} /></Field>
      </>}
      {notice && <p className="shared-notice" role="status">{notice}</p>}{error && <p className="shared-error" role="alert">{error}</p>}
      <button className="shared-primary" disabled={busy || tab === "profile" && avatarLoading}>{busy ? "正在保存…" : tab === "profile" ? "保存个人资料" : "更新密码"}</button>
    </form>
  </Modal>;
}

const avatarColors = ["#6C82AD", "#497BE0", "#8170AE", "#B17391", "#B7845D", "#9C795E", "#59677C", "#343A43"];
function AvatarDesigner({ profile, value, changed, loading }: { profile: Profile; value: AvatarChange; changed: (value: AvatarChange) => void; loading: (busy: boolean) => void }) {
  const canvas = useRef<HTMLCanvasElement>(null); const upload = useRef<HTMLInputElement>(null); const current = useRef(value); current.current = value;
  const [image, setImage] = useState<HTMLImageElement | null>(null); const [zoom, setZoom] = useState(1); const [offset, setOffset] = useState({ x: 0, y: 0 }); const [error, setError] = useState("");
  const drag = useRef<{ x: number; y: number; ox: number; oy: number } | null>(null); const epoch = useRef(0);
  useEffect(() => () => { epoch.current++; loading(false); }, [loading]);
  useEffect(() => {
    if (!image || !canvas.current) return;
    const context = canvas.current.getContext("2d"); if (!context) return;
    const scale = 256 / Math.min(image.naturalWidth, image.naturalHeight) * zoom; const width = image.naturalWidth * scale; const height = image.naturalHeight * scale;
    context.clearRect(0, 0, 256, 256); context.drawImage(image, (256 - width) / 2 + offset.x * (width - 256) / 2, (256 - height) / 2 + offset.y * (height - 256) / 2, width, height);
    let data = canvas.current.toDataURL("image/png");
    if (data.length > 174000) {
      const smaller = document.createElement("canvas"); smaller.width = smaller.height = 128;
      smaller.getContext("2d")?.drawImage(canvas.current, 0, 0, 128, 128); data = smaller.toDataURL("image/png");
    }
    changed({ ...current.current, imageData: data, removeImage: false }); loading(false);
  }, [image, zoom, offset, changed, loading]);
  const choose = (file?: File) => {
    if (!file) return; setError("");
    if (!["image/png", "image/jpeg", "image/webp"].includes(file.type) || file.size > 10 * 1024 * 1024) { setError("请选择 10 MB 以内的 PNG、JPG 或 WebP 图片"); return; }
    const version = ++epoch.current; const url = URL.createObjectURL(file); const next = new Image(); loading(true);
    next.onload = () => {
      URL.revokeObjectURL(url); if (version !== epoch.current) return;
      if (next.naturalWidth * next.naturalHeight > 32_000_000) { loading(false); setError("图片尺寸过大，请先缩小后再上传"); return; }
      setZoom(1); setOffset({ x: 0, y: 0 }); setImage(next);
    };
    next.onerror = () => { URL.revokeObjectURL(url); if (version === epoch.current) { loading(false); setError("无法读取这张图片，请换一张重试"); } }; next.src = url;
  };
  const removePhoto = () => { epoch.current++; setImage(null); loading(false); changed({ color: value.color, text: value.text, removeImage: true }); if (upload.current) upload.current.value = ""; };
  return <div className="shared-avatar-designer">
    <div className="shared-avatar-design-heading"><span>设计头像</span><small>图片或文字，都可以是你的标记</small></div>
    <div className="shared-avatar-design-body">
      {image ? <canvas width={256} height={256} ref={canvas} className="shared-avatar-crop" aria-label="头像裁剪预览，拖动调整位置"
        onPointerDown={event => { drag.current = { x: event.clientX, y: event.clientY, ox: offset.x, oy: offset.y }; event.currentTarget.setPointerCapture(event.pointerId); }}
        onPointerMove={event => { const start = drag.current; if (!start) return; const scale = 256 / Math.min(image.naturalWidth, image.naturalHeight) * zoom; const ratio = 256 / event.currentTarget.getBoundingClientRect().width; const clamp = (value: number) => Math.max(-1, Math.min(1, value)); setOffset({ x: clamp(start.ox + (event.clientX - start.x) * ratio / Math.max(1, (image.naturalWidth * scale - 256) / 2)), y: clamp(start.oy + (event.clientY - start.y) * ratio / Math.max(1, (image.naturalHeight * scale - 256) / 2)) }); }}
        onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }} />
        : <div className="shared-avatar-large"><UserAvatar name={profile.displayName} accountId={profile.id} avatar={{ color: value.color, text: value.text, imageVersion: value.removeImage ? null : profile.avatar?.imageVersion }} /></div>}
      <div className="shared-avatar-design-actions"><button type="button" className="shared-button" onClick={() => upload.current?.click()}><Icon name="plus" size={16} />上传头像</button><button type="button" className="shared-link" onClick={removePhoto}>使用文字头像</button><small>选择图片后可拖动、缩放裁剪</small></div>
    </div>
    <input className="shared-file-input" ref={upload} type="file" aria-label="上传头像图片" accept="image/png,image/jpeg,image/webp" onChange={event => choose(event.target.files?.[0])} />
    {image && <label className="shared-avatar-zoom">缩放<input type="range" min="1" max="3" step="0.05" value={zoom} onChange={event => setZoom(Number(event.target.value))} aria-label="头像缩放" /></label>}
    <div className="shared-avatar-personalize"><label>头像文字<input value={value.text} maxLength={8} placeholder="名字、字母或表情" onChange={event => changed({ ...value, text: event.target.value })} /></label>
      <div className="shared-avatar-colors" role="group" aria-label="头像颜色">{avatarColors.map(color => <button key={color} type="button" aria-label={`头像颜色 ${color}`} aria-pressed={value.color.toUpperCase() === color} style={{ background: color }} onClick={() => changed({ ...value, color })} />)}</div>
    </div>{error && <p className="shared-error" role="alert">{error}</p>}
  </div>;
}
