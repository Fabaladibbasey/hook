import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter, Route, Routes } from "react-router-dom";
import App from "./App";
import ChatRoom from "./routes/ChatRoom";
import DevConsole from "./routes/DevConsole";
import "./styles.css";

const root = createRoot(document.getElementById("root")!);
root.render(
  <StrictMode>
    <BrowserRouter>
      <Routes>
        <Route path="/" element={<App />} />
        <Route path="/c/:chatId/:token" element={<ChatRoom />} />
        <Route path="/dev" element={<DevConsole />} />
      </Routes>
    </BrowserRouter>
  </StrictMode>
);
