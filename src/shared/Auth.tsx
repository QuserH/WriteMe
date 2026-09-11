import { useState, type FormEvent } from "react";
import { api, type Profile } from "./api";
import { Field, Logo } from "./ui";

export function Auth({ profile, onLogin, initialError, admin = false }: { profile: Profile | null; onLogin: (profile: Profile) => void; initialError: string; admin?: boolean }) {
  const [error, setError] = useState(initialError); const [busy, setBusy] = useState(false);
  const submit = async (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault(); const data = new FormData(event.currentTarget); setBusy(true); setError("");
    try { const next = profile ? await api<Profile>("profile", "POST", { publicId: data.get("publicId"), displayName: data.get("displayName"), password: data.get("password") }) : await api<Profile>("session", "POST", { username: data.get("username"), password: data.get("password") }); onLogin(next); }
    catch (error) { setError(error instanceof Error ? error.message : "登录失败"); } finally { setBusy(false); }
  };
  return <div className="shared-app shared-auth"><header className="shared-auth-header"><Logo small /><span>{admin ? "管理控制台" : "共享工作区"}</span></header>
    <div className="shared-auth-form"><div className="shared-auth-card"><div className="shared-login-mark" aria-hidden="true">w<span>·</span></div><span className="shared-eyebrow">{profile ? "认识一下" : "欢迎回来"}</span><h2>{profile ? "设置你的个人名片" : admin ? "登录管理控制台" : "登录你的工作区"}</h2><p>{profile ? "设置姓名和个人 ID，让伙伴找到你。" : admin ? "管理账号与访问权限。" : "在网页与桌面，继续同一份工作。"}</p>
      <form onSubmit={submit}>{profile ? <><Field label="你的名字"><input name="displayName" defaultValue={profile.displayName} maxLength={40} autoComplete="name" placeholder="例如：小何" required autoFocus /></Field><Field label="个人 ID" hint="3–32 位字母、数字、下划线或短横线"><div className="shared-input-prefix"><span>@</span><input name="publicId" defaultValue={profile.publicId ?? ""} pattern="[a-zA-Z0-9][a-zA-Z0-9_-]{2,31}" maxLength={32} placeholder="your_name" required /></div></Field></> : <Field label="登录账号"><input name="username" autoComplete="username" placeholder="管理员为你开通的账号" required autoFocus maxLength={100} /></Field>}
      <Field label={profile ? "设置新密码" : "密码"} hint={profile ? "至少 12 个字符，可使用一句容易记住的短语" : undefined}><input name="password" type="password" autoComplete={profile ? "new-password" : "current-password"} minLength={profile ? 12 : 1} maxLength={1024} placeholder={profile ? "设置仅自己知道的密码" : "输入密码"} required /></Field>
      {error && <p className="shared-error" role="alert">{error}</p>}<button type="submit" className="shared-primary" aria-label={profile ? "进入工作区" : "登录"} disabled={busy}>{busy ? "请稍候…" : profile ? "进入工作区" : "登录"}<span aria-hidden="true">→</span></button></form><p className="shared-auth-help">{profile ? "设置完成后，就可以创建或加入共享工作区。" : "账号由管理员开通。如需重置密码，请联系管理员。"}</p></div><div className="shared-auth-foot"><span>WriteME</span><span>{admin ? "仅管理员可访问" : "网页与桌面使用同一账号"}</span></div></div></div>;
}
