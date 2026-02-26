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
                    bat """
                        robocopy ${sourceDir} "${PHYSICAL_PATH}" /MIR /XD .git node_modules /XF web.config .jenkins-env /NFL /NDL /NP
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
        }
        failure {
            echo "ERROR Deploy fallo para ${SITE_NAME}"
        }
    }
}
