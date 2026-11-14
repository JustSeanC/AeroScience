// Static display boundaries shared by inventory/migration guards. Resolve local
// helper signatures in their lexical scope instead of assuming every select or
// field helper has the shared parent/title/options/value argument order.
const defaults={node:{display:[1]},el:{display:[1]},e:{display:[1]},text:{display:[1]},element:{display:[1]},
 button:{display:[1]},link:{display:[1]},action:{display:[0]},field:{display:[1],values:[2,3]},formField:{display:[1]},
 select:{display:[1,2],options:2,values:[3]},disclosure:{display:[1,2]},informationDetails:{display:[1]},
 metric:{display:[1,2,3]},table:{display:[1,2],values:[3]},chart:{display:[1,3]},legend:{display:[1,6]},
 heading:{display:[0,1]},modal:{display:[0]},submit:{display:[1]},status:{display:[1]},notice:{display:[0]},
 confirm:{display:[0]},alert:{display:[0]},dialogHeader:{display:[1]},statusPill:{display:[1]},
 nextAction:{display:[1]},emptyState:{display:[1]},scientificNotice:{display:[2]},setPresentationText:{display:[1]},presentationTextNode:{display:[0]},createTextNode:{display:[0]},label:{display:[0]}};
const prop=n=>n?.type==='Identifier'?n.name:n?.type==='MemberExpression'?prop(n.property):n?.value;
export function presentationContracts(ast,source){
 const declarations=[];
 function visit(n,parents){if(!n||!n.type)return;
  if(/Function/.test(n.type)){
   const parent=parents.at(-1),name=n.id?.name||(parent?.type==='VariableDeclarator'?parent.id.name:null);
   const parameters=n.params.map(p=>prop(p.type==='AssignmentPattern'?p.left:p));
   const code=source.slice(n.body.start,n.body.end),ui=/createElement|textContent|setPresentationText|\b(?:node|el|element|field|formField|button|link)\(/.test(code);
   if(name&&ui){const display=[],values=[];for(const[index,parameter]of parameters.entries()){
     if(/^(?:label|title|text|message|caption|description|help|heading|headings|subtitle|options|choices)$/.test(parameter)||parameter==='name'&&['input','field','formField','heading','button','link'].includes(name))display.push(index);
     if(/^(?:value|type|className|kind|selected|selectedValue)$/.test(parameter))values.push(index);
    }
    if(display.length){const options=parameters.findIndex(p=>/^(?:options|choices)$/.test(p));const scope=parents.findLast(p=>/Function/.test(p.type)||p.type==='Program');declarations.push({name,scope,contract:{display,values,...(options>=0?{options}:{})}});}
   }
  }
  for(const[k,v]of Object.entries(n))if(!['start','end','loc'].includes(k)){if(Array.isArray(v))v.forEach(c=>visit(c,[...parents,n]));else if(v&&typeof v==='object')visit(v,[...parents,n]);}
 }
 visit(ast,[]);
 return(name,call)=>declarations.filter(d=>d.name===name&&d.scope.start<=call.start&&d.scope.end>=call.end).sort((a,b)=>(a.scope.end-a.scope.start)-(b.scope.end-b.scope.start))[0]?.contract||defaults[name];
}
export const argumentPosition=(call,node)=>call.arguments.findIndex(argument=>argument.start<=node.start&&argument.end>=node.end);
