"use strict";
// 2.10.0: Online e sito on Un PC solo. Inside the program's window the switch asks Windows for permission and
// reconfigures this PC; in a browser it shows how to do it by hand.
(() => {
 const button=document.querySelector("[data-online]");if(!button)return;
 const status=document.querySelector("[data-online-status]"),manual=document.querySelector("[data-online-manual]"),host=window.chrome&&window.chrome.webview;
 button.addEventListener("click",()=>{
  if(!host){manual.hidden=false;return;}
  button.disabled=true;status.textContent="Conferma nella finestra di Windows che chiede l'autorizzazione. Poi il programma riparte e la pagina si aggiorna da sola.";
  host.postMessage("tdwin-online:"+button.dataset.online);
 });
 host?.addEventListener("message",e=>{if(typeof e.data==="string"&&e.data.startsWith("tdwin-online-error:")){button.disabled=false;status.textContent=e.data.slice(19);}});
})();
