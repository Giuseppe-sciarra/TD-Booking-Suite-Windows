// The passkey of the program's window, made and used in the browser (app.js does the same for the web pages):
// the window opens this page with a ticket and goes on by itself when the page says it is done.
(()=>{
 const box=document.getElementById("passkey");if(!box)return;
 const $=id=>document.getElementById(id),ticket=box.dataset.ticket,register=box.dataset.kind==="register";
 const bytes=s=>Uint8Array.from(atob(s.replace(/-/g,"+").replace(/_/g,"/")),c=>c.charCodeAt(0));
 const encoded=b=>btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g,"-").replace(/\//g,"_").replace(/=+$/,"");
 const request=async(step,data)=>{
  const r=await fetch("/passkey/"+ticket+"/"+step,{method:"POST",headers:{"Content-Type":"application/json"},body:JSON.stringify(data)});
  let result;try{result=await r.json();}catch{throw Error("Operazione non disponibile. Riparti dal programma.");}
  if(!r.ok)throw Error(result.error||"Operazione non consentita.");return result;
 };
 async function go(){
  const output=$("auth-status"),button=$("passkey-go");
  output.textContent="Segui le istruzioni del dispositivo.";button.disabled=true;let done=false;
  try{
   if(!window.PublicKeyCredential||!navigator.credentials)throw Error("Questo browser non supporta le passkey. Usa Authenticator.");
   const options=(await request("begin",{})).options;options.challenge=bytes(options.challenge);
   let credential;
   if(register){
    options.user.id=bytes(options.user.id);
    options.excludeCredentials=(options.excludeCredentials||[]).map(x=>({...x,id:bytes(x.id)}));
    credential=await navigator.credentials.create({publicKey:options});
   }else{
    options.allowCredentials=(options.allowCredentials||[]).map(x=>({...x,id:bytes(x.id)}));
    credential=await navigator.credentials.get({publicKey:options});
   }
   const r=credential.response,response={clientDataJSON:encoded(r.clientDataJSON)};
   if(r.attestationObject){response.attestationObject=encoded(r.attestationObject);response.transports=r.getTransports?.()||[];}
   else{response.authenticatorData=encoded(r.authenticatorData);response.signature=encoded(r.signature);response.userHandle=r.userHandle?encoded(r.userHandle):null;}
   const result=await request("finish",{name:$("key-name")?.value||"Passkey",credential:{id:credential.id,rawId:encoded(credential.rawId),type:credential.type,response}});
   done=true;button.hidden=true;if($("key-name"))$("key-name").disabled=true;
   output.textContent=register?"Passkey aggiunta. Puoi chiudere questa pagina e tornare al programma.":"Fatto. Puoi chiudere questa pagina: il programma prosegue da solo.";
   if(result.codes&&result.codes.length){for(const c of result.codes){const li=document.createElement("li");li.textContent=c;$("passkey-code-list").append(li);}$("passkey-codes").hidden=false;}
  }catch(e){output.textContent=e.name==="NotAllowedError"?"Operazione annullata o scaduta. Puoi riprovare.":e.message;}
  finally{if(!done)button.disabled=false;}
 }
 $("passkey-go").addEventListener("click",go);
})();
