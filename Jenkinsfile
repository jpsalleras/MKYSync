// Jenkinsfile - Template para proyectos .NET Core / .NET Framework
// HTTPS por defecto

pipeline {
    agent any

    triggers {
        githubPush()
    }

    stages {

        stage('Checkout') {
            steps {
                checkout scm
            }
        }

        stage('Check Skip CI') {
            steps {
                script {
                    def lastCommit = bat(script: '@git log -1 --pretty=%%s', returnStdout: true).trim()
                    if (lastCommit.contains('[ci skip]')) {
                        currentBuild.result = 'NOT_BUILT'
                        error('Skipping build - commit contains [ci skip]')
                    }
                }
            }
        }

        stage('Cargar Configuracion') {
            steps {
                script {
                    // Resolver wildcard *.sln al archivo real
                    if (env.SOLUTION_FILE?.contains('*')) {
                        def found = bat(script: "@dir /b ${env.SOLUTION_FILE} 2>nul", returnStdout: true).trim()
                        if (found) {
                            env.SOLUTION_FILE = found.split('\n')[0].trim()
                            echo "OK Solucion encontrada: ${env.SOLUTION_FILE}"
                        } else {
                            error("No se encontro archivo que coincida con ${env.SOLUTION_FILE}")
                        }
                    }

                    // Resolver ruta completa del proyecto a publicar si es solo un nombre de archivo
                    if (env.PUBLISH_PROJECT?.trim() && !env.PUBLISH_PROJECT.contains('\\') && !env.PUBLISH_PROJECT.contains('/')) {
                        def found = bat(script: "@dir /s /b ${env.PUBLISH_PROJECT} 2>nul", returnStdout: true).trim()
                        if (found) {
                            def fullPath = found.split('\n')[0].trim()
                            // Convertir ruta absoluta a relativa al workspace
                            env.PUBLISH_PROJECT = fullPath.replace(env.WORKSPACE + '\\', '')
                            echo "OK Proyecto a publicar: ${env.PUBLISH_PROJECT}"
                        }
                    }

                    echo """
                    Configuracion:
                      Tipo:     ${env.PROJECT_TYPE}
                      Solucion: ${env.SOLUTION_FILE}
                      Publish:  ${env.PUBLISH_PROJECT ?: '(solucion completa)'}
                      Sitio:    ${env.SITE_NAME}
                      Dominio:  https://${env.DOMAIN}
                      Path:     ${env.PHYSICAL_PATH}
                      AppPool:  ${env.APP_POOL}
                    """
                }
            }
        }

        // ===================================
        // INCREMENTAR VERSION
        // ===================================
        stage('Incrementar Version') {
            when {
                expression { env.INCREMENT_VERSION == 'true' }
            }
            steps {
                powershell '''
                    $versionUpdated = $false

                    # .NET Framework: buscar AssemblyInfo.vb o .cs
                    $assemblyInfoFiles = Get-ChildItem -Path . -Include AssemblyInfo.vb,AssemblyInfo.cs -Recurse
                    foreach ($file in $assemblyInfoFiles) {
                        $content = Get-Content $file.FullName -Raw
                        $regex = 'AssemblyVersion\\("(\\d+)\\.(\\d+)\\.(\\d+)\\.(\\d+)"\\)'
                        if ($content -match $regex) {
                            $newRev = [int]$Matches[4] + 1
                            $newVer = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$newRev"
                            $content = $content -replace 'AssemblyVersion\\("\\d+\\.\\d+\\.\\d+\\.\\d+"\\)', "AssemblyVersion(`"$newVer`")"
                            $content = $content -replace 'AssemblyFileVersion\\("\\d+\\.\\d+\\.\\d+\\.\\d+"\\)', "AssemblyFileVersion(`"$newVer`")"
                            Set-Content $file.FullName $content -NoNewline
                            Write-Host "OK Version incrementada a $newVer en $($file.Name)"
                            $versionUpdated = $true
                        }
                    }

                    # .NET Core: buscar Version en .csproj
                    $csprojFiles = Get-ChildItem -Path . -Filter *.csproj -Recurse
                    foreach ($file in $csprojFiles) {
                        [xml]$xml = Get-Content $file.FullName
                        $versionNode = $xml.SelectSingleNode("//Version")
                        if ($versionNode) {
                            $parts = $versionNode.InnerText.Split('.')
                            $parts[$parts.Length - 1] = [int]$parts[$parts.Length - 1] + 1
                            $newVer = $parts -join '.'
                            $versionNode.InnerText = $newVer
                            $xml.Save($file.FullName)
                            Write-Host "OK Version incrementada a $newVer en $($file.Name)"
                            $versionUpdated = $true
                        }
                    }

                    if (-not $versionUpdated) {
                        Write-Host "WARN No se encontraron archivos de version para incrementar"
                    }
                '''
            }
        }

        // ===================================
        // BUILD .NET Core / .NET 8
        // ===================================
        stage('Build - .NET Core') {
            when {
                expression { env.PROJECT_TYPE == 'dotnet-core' }
            }
            steps {
                bat """
                    dotnet restore ${SOLUTION_FILE}
                    dotnet build ${SOLUTION_FILE} --configuration Release --no-restore
                """
            }
        }

        stage('Publish - .NET Core') {
            when {
                expression { env.PROJECT_TYPE == 'dotnet-core' }
            }
            steps {
                script {
                    // Si PUBLISH_PROJECT tiene valor, publicar solo ese proyecto; sino publicar la solucion
                    def publishTarget = env.PUBLISH_PROJECT?.trim() ? env.PUBLISH_PROJECT : env.SOLUTION_FILE
                    bat """
                        dotnet publish ${publishTarget} --configuration Release --output .\\publish
                    """
                }
            }
        }

        // ===================================
        // BUILD .NET Framework (MSBuild)
        // ===================================
        stage('Build - .NET Framework') {
            when {
                expression { env.PROJECT_TYPE == 'dotnet-framework' }
            }
            steps {
                script {
                    def buildTarget = env.PUBLISH_PROJECT?.trim() ? env.PUBLISH_PROJECT : env.SOLUTION_FILE
                    bat """
                        nuget restore ${SOLUTION_FILE}
                        msbuild ${buildTarget} /p:Configuration=Release /p:DeployOnBuild=true /p:PublishProfile=FolderProfile /p:publishUrl=.\\publish
                    """
                }
            }
        }

        // ===================================
        // BUILD Frontend estatico (npm)
        // ===================================
        stage('Build - Static') {
            when {
                expression { env.PROJECT_TYPE == 'static' }
            }
            steps {
                bat '''
                    npm ci
                    npm run build
                '''
            }
        }

        // ===================================
        // DEPLOY
        // ===================================
        stage('Stop Site') {
            steps {
                powershell """
                    Import-Module WebAdministration
                    if (Get-Website -Name '${SITE_NAME}' -ErrorAction SilentlyContinue) {
                        Stop-Website -Name '${SITE_NAME}' -ErrorAction SilentlyContinue
                        Stop-WebAppPool -Name '${APP_POOL}' -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 2
                    }
                """
            }
        }

        stage('Deploy Files') {
            steps {
                script {
                    def sourceDir = (env.PROJECT_TYPE == 'static') ? '.\\dist' : '.\\publish'
                    // .NET Framework: excluir web.config (tiene connection strings)
                    // .NET Core: incluir web.config (AspNetCoreModuleV2) pero excluir appsettings (tiene connection strings)
                    def excludeFiles = (env.PROJECT_TYPE == 'dotnet-framework') ? '/XF web.config .jenkins-env' : '/XF appsettings.json appsettings.*.json .jenkins-env'
                    bat """
                        robocopy ${sourceDir} "${PHYSICAL_PATH}" /MIR /XD .git node_modules ${excludeFiles} /NFL /NDL /NP
                        exit /b 0
                    """
                }
            }
        }

        stage('Start Site') {
            steps {
                powershell """
                    Import-Module WebAdministration
                    Start-WebAppPool -Name '${APP_POOL}'
                    Start-Website -Name '${SITE_NAME}'
                    Write-Host 'OK Sitio iniciado: https://${DOMAIN}'
                """
            }
        }

        stage('Health Check') {
            steps {
                script {
                    sleep(time: 5, unit: 'SECONDS')
                    try {
                        powershell """
                            # Ignorar errores de certificado en caso de que el DNS no resuelva aun
                            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = { \$true }

                            \$response = Invoke-WebRequest -Uri 'https://${DOMAIN}' -UseBasicParsing -TimeoutSec 10
                            if (\$response.StatusCode -eq 200) {
                                Write-Host 'OK Health check - Status 200'
                            } else {
                                Write-Host 'WARN Status: ' + \$response.StatusCode
                            }
                        """
                    } catch (Exception e) {
                        echo "WARN Health check fallo (puede ser normal si requiere configuracion adicional): ${e.message}"
                    }
                }
            }
        }
    }

    post {
        success {
            echo "OK Deploy exitoso: https://${DOMAIN}"
            script {
                // Commit + push version increment
                if (env.INCREMENT_VERSION == 'true') {
                    try {
                        bat """
                            git config user.email "jenkins@deploy.local"
                            git config user.name "Jenkins Deploy"
                            git add -A
                            git diff --cached --quiet || git commit -m "Auto-increment version [ci skip]"
                        """
                        withCredentials([usernamePassword(
                            credentialsId: env.GIT_CREDENTIALS_ID ?: '36dbc848-51a4-410d-bccc-54b7b265a9fb',
                            usernameVariable: 'GIT_USER',
                            passwordVariable: 'GIT_PASS'
                        )]) {
                            bat "git push https://%GIT_USER%:%GIT_PASS%@github.com/${env.GITHUB_REPO_PATH}.git HEAD:${env.DEPLOY_BRANCH ?: 'develop'}"
                        }
                        echo "OK Version commit + push completado"
                    } catch (Exception e) {
                        echo "WARN No se pudo hacer push del incremento de version: ${e.message}"
                    }
                }
                // Email notification
                if (env.MAIL_SUCCESS?.trim()) {
                    mail to: env.MAIL_SUCCESS,
                         subject: "Deploy Exitoso: ${env.SITE_NAME}",
                         body: "Deploy exitoso para ${env.SITE_NAME}\nURL: https://${env.DOMAIN}\nFecha: ${new Date().format('dd/MM/yyyy HH:mm')}"
                }
            }
        }
        failure {
            echo "ERROR Deploy fallo para ${SITE_NAME}"
            script {
                if (env.MAIL_FAILURE?.trim()) {
                    mail to: env.MAIL_FAILURE,
                         subject: "Deploy Fallido: ${env.SITE_NAME}",
                         body: "El deploy fallo para ${env.SITE_NAME}.\nURL: https://${env.DOMAIN}\nRevisar logs en Jenkins: ${env.BUILD_URL}"
                }
            }
        }
    }
}
