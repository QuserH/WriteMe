import React from "react";
import ReactDOM from "react-dom/client";
import "./styles.css";

const App = React.lazy(() => import("./App"));
const SharedApp = React.lazy(() => import("./shared/SharedApp"));
const AdminApp = React.lazy(() => import("./admin/AdminApp"));

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <React.Suspense fallback={<div className="app-loading">正在打开 WriteME…</div>}>
      {/^\/admin(?:\/|$)/.test(location.pathname) ? <AdminApp /> : location.pathname.startsWith("/team") ? <SharedApp /> : <App />}
    </React.Suspense>
  </React.StrictMode>,
);
