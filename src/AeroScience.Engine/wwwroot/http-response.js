import {t as textForUI} from './i18n.js';
// A committed mutation must never be retried merely because a success has no body.
export async function readSuccessResponse(response){
 const content=await response.text();
 if(!content.trim())return null;
 if(!(response.headers.get('content-type')||'').includes('json'))return content;
 try{return JSON.parse(content);}catch{throw new Error(textForUI("ui.httpResponse.messageTheCommandCompletedButItsResponseCould"));}
}
