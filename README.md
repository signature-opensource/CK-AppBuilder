# CK-AppBuilder

The App builder of CKomposable: it builds the Apps of a solution (a `.App` project to its `.Host`) from their
configuration (`ckonf.json`), and the deployments of a solution. The declarations that it reads are in
[CK.AppBuilder.Abstractions](https://github.com/signature-opensource/CK-Abstractions/blob/stable/CK.AppBuilder.Abstractions/README.md),
its model (`AppConfiguration`, the central configuration, the contributor contract) is in
[CK.Engine.Configuration](https://github.com/signature-opensource/CK-ResourceSpace/blob/stable/CK.Engine.Configuration/AppBuilder/README.md).

| Project | What it is |
|---|---|
| [CK.AppBuilder](CK.AppBuilder/README.md) | The runner of a `.Builder` project: reads its `ckonf.json`, creates and applies the aspects, runs the contributors (ordered by their requirements) and the engine. |
| [CK.AppDeployment](CK.AppDeployment/README.md) | Builds the deployments of a solution (the `#` folders of its `Deployment` folder): one Builder and its hosts per AppContext of a deployment. |
