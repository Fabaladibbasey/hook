import { useEffect, useState } from "react";

export default function RevokedToast() {
  const [show, setShow] = useState(true);

  useEffect(() => {
    const timer = setTimeout(() => setShow(false), 3000);
    return () => clearTimeout(timer);
  }, []);

  if (show) {
    return (
      <div className="min-h-screen flex items-center justify-center p-6">
        <div className="rounded-xl bg-amber-50 border border-amber-200 p-4 max-w-md text-amber-900 text-sm">
          Session opened on another device — disconnecting this one.
        </div>
      </div>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center p-6">
      <div className="text-slate-600 text-center max-w-md">
        Session ended (opened elsewhere).
      </div>
    </div>
  );
}
