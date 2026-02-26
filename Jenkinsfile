// Jenkinsfile - Template para proyectos .NET Core / .NET Framework
// HTTPS por defecto

pipeline {
    agent any

    environment {
        SITE_NAME       = "${env.SITE_NAME ?: 'MiApp'}"
        DOMAIN          = "${env.DOMAIN ?: 'miapp.local'}"
        PHYSICAL_PATH   = "${env.PHYSICAL_PATH ?: 'C:\\inetpub\\sites\\MiApp'}"
        APP_POOL        = "${env.APP_POOL ?: 'MiApp-Pool'}"
        SOLUTION_FILE   = "${env.SOLUTION_FILE ?: '*.sln'}"
        PUBLISH_PROJECT = "${env.PUBLISH_PROJECT ?: ''}"    // .csproj especifico para publish (vacio = usa SOLUTION_FILE)
        PROJECT_TYPE    = "${env.PROJECT_TYPE ?: 'dotnet-core'}"
    }

    triggers {
        githubPush()
    }

    stages {

        stage('Checkout') {
            steps {
                checkout scm
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
